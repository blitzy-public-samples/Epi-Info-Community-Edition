import { useCallback, useEffect, useId, useMemo, useState, type KeyboardEvent, type ReactNode } from 'react';

import type { ApiClient } from '../api/client';
import { describeError, isAbortError, networkError, type ApiError } from '../api/errors';
import type { FieldDefinition, FieldValue, FormDefinition, RecordPage, RecordSummary } from '../api/types';
import { ConnectionError } from './ConnectionError';

export interface RecordGridProps {
  client: ApiClient;
  viewId: number;
  /** The open form; its fields name the column headers. */
  definition: FormDefinition;
  /** The shared deleted toggle, owned by the navigation state. */
  includeDeleted: boolean;
  onIncludeDeletedChange: (value: boolean) => void;
  /** Bumped whenever the record list may have changed; each change reloads the current page. */
  listVersion: number;
  /** `globalRecordId` of the open record, marked `aria-current`. */
  openRecordId?: string | null;
  /** Opens a record by id; the caller runs the leave flow first. */
  onOpen: (globalRecordId: string) => void;
  /** Makes every control inert, for example while a save is in flight. */
  disabled?: boolean;
}

const PAGE_SIZE = 50;
const SEARCH_DEBOUNCE_MS = 300;

const LOADING_TEXT = 'Loading records…';
const EMPTY_TEXT = 'No records found.';
// The 403, 404, 409, 413 and 415 texts of describeError speak of a record or a write;
// none of them describes a records-list GET, so the grid states its own fixed text.
const LIST_FAILED_TEXT = 'The record list could not be loaded.';
// Column of last resort, for a form whose server-chosen grid columns are empty: a row
// with no cell would be a focusable row with no content and no accessible name.
const RECORD_ID_COLUMN = 'Record ID';

// Paging names avoid First, Previous, Next, Last, New, Save, Delete and Find, which the
// navigator and toolbar own: Playwright matches accessible names by substring.
const EARLIER_TEXT = 'Earlier records';
const LATER_TEXT = 'Later records';

/** Header text of a grid column: the field's non-blank prompt, else its name, else the key itself. */
function headerText(key: string, fields: ReadonlyMap<string, FieldDefinition>): string {
  const field = fields.get(key.toLowerCase());
  if (field === undefined) {
    return key;
  }
  const prompt = field.prompt;
  return typeof prompt === 'string' && prompt.trim() !== '' ? prompt : field.name;
}

/** Display text of a cell; a missing or `null` value is blank, never "null" or "undefined". */
function cellText(value: FieldValue | undefined): string {
  if (typeof value === 'boolean') {
    return value ? 'Yes' : 'No';
  }
  if (typeof value === 'number') {
    return String(value);
  }
  if (typeof value === 'string') {
    return value;
  }
  return '';
}

function pageCountOf(result: RecordPage): number {
  const pageSize = result.pageSize > 0 ? result.pageSize : PAGE_SIZE;
  return Math.max(1, Math.ceil(result.total / pageSize));
}

function countText(total: number): string {
  return total === 1 ? '1 record' : `${total} records`;
}

function isConnectionFailure(error: ApiError): boolean {
  return error.kind === 'unavailable' || error.kind === 'network';
}

function listErrorText(error: ApiError): string {
  switch (error.kind) {
    case 'badRequest':
    case 'validation':
    case 'server':
      return describeError(error);
    default:
      return LIST_FAILED_TEXT;
  }
}

function sameRecordId(a: string, b: string | null | undefined): boolean {
  return typeof b === 'string' && a.toLowerCase() === b.toLowerCase();
}

interface ListErrorBannerProps {
  error: ApiError;
  onRetry: () => void;
}

/** A failed list load other than an unreachable server; a bad request is not worth repeating. */
function ListErrorBanner({ error, onRetry }: ListErrorBannerProps) {
  const message = listErrorText(error);
  const traceId = error.traceId;
  // describeError already ends a 500's text with the trace id.
  const traceLine = traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;
  const canRetry = error.kind !== 'badRequest' && error.kind !== 'validation';

  return (
    <div className="banner banner--error" role="alert">
      <p>{message}</p>
      {traceLine !== null && (
        <p>
          <small>{traceLine}</small>
        </p>
      )}
      {canRetry && (
        <button type="button" onClick={onRetry}>
          Retry
        </button>
      )}
    </div>
  );
}

/** The records page on screen, tagged with its form so another form's rows never show. */
interface ShownPage {
  viewId: number;
  page: RecordPage;
}

/**
 * The searchable, pageable record list of the open form. It displays what
 * `GET /api/forms/{viewId}/records` returns and sends nothing else: the search box is
 * the server's case-insensitive "contains" over the grid columns, debounced, and the
 * server also chooses the columns. Activating a row asks the caller to open that
 * record, which runs the editor's leave flow before anything changes.
 */
export function RecordGrid({
  client,
  viewId,
  definition,
  includeDeleted,
  onIncludeDeletedChange,
  listVersion,
  openRecordId = null,
  onOpen,
  disabled = false,
}: RecordGridProps) {
  const searchId = useId();
  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [page, setPage] = useState(1);
  const [shown, setShown] = useState<ShownPage | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [reloadToken, setReloadToken] = useState(0);

  const searchText = debouncedQ.trim();

  // A new filter or form starts again at page 1. Adjusted during render rather than in
  // an effect, so the load effect never requests the old page under the new filter.
  const filterKey = JSON.stringify([viewId, includeDeleted, searchText]);
  const [pageFilterKey, setPageFilterKey] = useState(filterKey);
  if (pageFilterKey !== filterKey) {
    setPageFilterKey(filterKey);
    setPage(1);
  }

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQ(q), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [q]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);

    void client
      .listRecords(
        viewId,
        { q: searchText === '' ? undefined : searchText, includeDeleted, page, pageSize: PAGE_SIZE },
        controller.signal,
      )
      .then(
        (outcome) => {
          if (controller.signal.aborted) {
            return;
          }
          if (!outcome.ok) {
            setError(outcome.error);
            setLoading(false);
            return;
          }
          // The list shrank under a later page (a delete, a narrower filter): show its
          // last page instead of an empty one. The page only decreases, so this settles.
          const lastPage = pageCountOf(outcome.data);
          if (outcome.data.items.length === 0 && page > lastPage) {
            setPage(lastPage);
            return;
          }
          setShown({ viewId, page: outcome.data });
          setError(null);
          setLoading(false);
        },
        (e: unknown) => {
          if (controller.signal.aborted || isAbortError(e)) {
            return;
          }
          // The client settles every failure into a result, so a rejection means no
          // response was confirmed; GET repeats safely through Retry.
          setError(networkError());
          setLoading(false);
        },
      );

    return () => controller.abort();
  }, [client, viewId, searchText, includeDeleted, page, listVersion, reloadToken]);

  const reload = useCallback(() => setReloadToken((token) => token + 1), []);

  const fieldsByName = useMemo(() => {
    const map = new Map<string, FieldDefinition>();
    for (const pageDefinition of definition.pages) {
      for (const field of pageDefinition.fields) {
        const key = field.name.toLowerCase();
        if (!map.has(key)) {
          map.set(key, field);
        }
      }
    }
    return map;
  }, [definition]);

  const result = shown !== null && shown.viewId === viewId ? shown.page : null;
  const items: readonly RecordSummary[] = result?.items ?? [];
  const columns = useMemo(() => {
    const first = items[0];
    return first === undefined ? [] : Object.keys(first.values);
  }, [items]);
  const lastPage = result === null ? 1 : pageCountOf(result);

  const open = (globalRecordId: string) => {
    if (!disabled) {
      onOpen(globalRecordId);
    }
  };

  const handleRowKeyDown = (event: KeyboardEvent<HTMLTableRowElement>, globalRecordId: string) => {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    if (event.key === 'Enter' || event.key === ' ') {
      // Space would otherwise scroll the page.
      event.preventDefault();
      open(globalRecordId);
    }
  };

  const handleSearchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // Enter applies the search at once instead of after the pause.
    if (event.key === 'Enter') {
      setDebouncedQ(q);
    }
  };

  // A load that follows a failure replaces the banner with the loading text, so Retry
  // and a new search visibly run, and a repeated failure announces its alert again.
  // Retry stays available while the grid is disabled: it only repeats the list GET.
  let body: ReactNode;
  if (error !== null && !loading) {
    body = isConnectionFailure(error) ? (
      <ConnectionError error={error} method="GET" onRetry={reload} />
    ) : (
      <ListErrorBanner error={error} onRetry={reload} />
    );
  } else if (result === null || error !== null) {
    body = <p>{LOADING_TEXT}</p>;
  } else if (items.length === 0) {
    body = <p>{EMPTY_TEXT}</p>;
  } else {
    const hasColumns = columns.length > 0;
    body = (
      <table aria-busy={loading}>
        <caption className="visually-hidden">{`${definition.name} records`}</caption>
        <thead>
          <tr>
            {hasColumns ? (
              columns.map((key) => (
                <th key={key} scope="col">
                  {headerText(key, fieldsByName)}
                </th>
              ))
            ) : (
              <th scope="col">{RECORD_ID_COLUMN}</th>
            )}
          </tr>
        </thead>
        <tbody>
          {items.map((row) => {
            const deleted = row.recStatus === 0;
            const deletedMark = deleted ? <span className="visually-hidden"> (deleted)</span> : null;
            return (
              <tr
                key={row.globalRecordId}
                tabIndex={0}
                className={deleted ? 'record-grid__row--deleted' : undefined}
                aria-current={sameRecordId(row.globalRecordId, openRecordId) ? 'true' : undefined}
                aria-disabled={disabled ? 'true' : undefined}
                onClick={() => open(row.globalRecordId)}
                onKeyDown={(event) => handleRowKeyDown(event, row.globalRecordId)}
              >
                {hasColumns ? (
                  columns.map((key, index) => (
                    <td key={key}>
                      {cellText(row.values[key])}
                      {index === 0 && deletedMark}
                    </td>
                  ))
                ) : (
                  <td>
                    {row.globalRecordId}
                    {deletedMark}
                  </td>
                )}
              </tr>
            );
          })}
        </tbody>
      </table>
    );
  }

  return (
    <section className="record-grid" aria-label="Records">
      <label htmlFor={searchId}>Search records</label>
      <input
        id={searchId}
        type="search"
        autoComplete="off"
        value={q}
        disabled={disabled}
        onChange={(event) => setQ(event.target.value)}
        onKeyDown={handleSearchKeyDown}
      />
      <label>
        <input
          type="checkbox"
          checked={includeDeleted}
          disabled={disabled}
          onChange={(event) => onIncludeDeletedChange(event.target.checked)}
        />
        Show deleted
      </label>
      {body}
      {error === null && result !== null && (
        <footer>
          <p aria-live="polite">{countText(result.total)}</p>
          {items.length > 0 && (
            <nav className="record-grid__paging" aria-label="Record list pages">
              <p>{`Page ${result.page} of ${lastPage}`}</p>
              <button
                type="button"
                disabled={disabled || page <= 1}
                onClick={() => setPage((current) => Math.max(1, current - 1))}
              >
                {EARLIER_TEXT}
              </button>
              <button
                type="button"
                disabled={disabled || page >= lastPage}
                onClick={() => setPage((current) => Math.min(lastPage, current + 1))}
              >
                {LATER_TEXT}
              </button>
            </nav>
          )}
        </footer>
      )}
    </section>
  );
}
