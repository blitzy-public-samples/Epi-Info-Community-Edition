import {
  memo,
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from 'react';

import { isBlankQuery, isExactDecimal, type ApiClient } from '../api/client';
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
  /**
   * Asks for the other set. The toggle shows the requested state as pending, and rows
   * cannot be opened, until the returned promise settles and `includeDeleted` follows.
   */
  onIncludeDeletedChange: (value: boolean) => Promise<void>;
  /** Bumped whenever the record list may have changed; each change reloads the current page. */
  listVersion: number;
  /** `globalRecordId` of the open record, marked `aria-current`. */
  openRecordId?: string | null;
  /** Opens a record by id; the caller runs the leave flow first. */
  onOpen: (globalRecordId: string) => void;
  /**
   * Called when the list answers 404, which means the form itself no longer exists; the
   * caller's form-not-found handling decides what happens to the open record.
   */
  onFormNotFound: () => void;
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
// A request aborted by something other than the grid, such as the browser stopping the
// page's requests, received no response and failed nothing: a plain line, not an alert.
const NOT_LOADED_TEXT = 'The record list was not loaded.';
// A list 404 means the form is gone. The workspace raises the alert and decides what
// follows, so the grid only states it, with nothing to retry.
const FORM_NOT_FOUND_TEXT = 'This form no longer exists.';
const RETRY_TEXT = 'Retry';
// Each row's open button is named by this prefix and its first cell's text, never by cell
// data alone, and the prefix holds none of the toolbar's and navigator's names.
const OPEN_RECORD_PREFIX = 'Open record ';
// An open button's name holds at most this many characters of its cell's text, so a
// 4000-character value is not read out in full. A grid column, even one spanning the whole
// content width, shows fewer characters of ordinary text, so the name keeps the visible text.
const NAME_TEXT_MAX = 150;
// The visible label of an open button whose first cell is blank, which its name starts with.
const OPEN_FALLBACK_TEXT = 'Open';
// The search box's query parameter, which keys its messages in a 400's `errors` map.
const SEARCH_PARAMETER = 'q';
// Column of last resort, for a form whose server-chosen grid columns are empty: a row
// with no cell would have no content and no cell to hold its open button.
const RECORD_ID_COLUMN = 'Record ID';

// Paging names avoid First, Previous, Next, Last, New, Save, Delete and Find, which the
// navigator and toolbar own: Playwright matches accessible names by substring.
const EARLIER_TEXT = 'Earlier records';
const LATER_TEXT = 'Later records';
// Name of the scrolling region around the table, whose own name is its caption. Not
// "Records", the section's exact name, and free of the names above for the same reason.
const SCROLL_REGION_LABEL = 'Record list';

/** The form's fields by exact name, and by lower-cased name where only one field name folds to it. */
interface FieldLookup {
  readonly exact: ReadonlyMap<string, FieldDefinition>;
  /** `null` for a lower-cased name that two or more distinct field names share. */
  readonly folded: ReadonlyMap<string, FieldDefinition | null>;
}

/**
 * Header text of a grid column: the field's non-blank prompt, else its name, else the key
 * itself. A column key is the server's canonical field name, so the field of exactly that
 * name labels it; a key that differs only in letter case names a field only when no other
 * field name shares its lower-cased form, as the server itself resolves names.
 */
function headerText(key: string, fields: FieldLookup): string {
  const field = fields.exact.get(key) ?? fields.folded.get(key.toLowerCase()) ?? undefined;
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
  // A bigint is an exact integer beyond the safe range, shown digit for digit.
  if (typeof value === 'number' || typeof value === 'bigint') {
    return String(value);
  }
  if (typeof value === 'string') {
    return value;
  }
  // An exact decimal is a typed number, shown as its JSON literal; no response carries one.
  if (isExactDecimal(value)) {
    return value.literal;
  }
  return '';
}

/**
 * The cell text an open button shows and is named by: the text itself, or past
 * NAME_TEXT_MAX characters its first NAME_TEXT_MAX and an ellipsis. Characters are code
 * points, so a surrogate pair is never split.
 */
function nameTextOf(text: string): string {
  let end = 0;
  let count = 0;
  for (const character of text) {
    if (count === NAME_TEXT_MAX) {
      return `${text.slice(0, end)}…`;
    }
    end += character.length;
    count += 1;
  }
  return text;
}

function pageCountOf(result: RecordPage): number {
  const pageSize = result.pageSize > 0 ? result.pageSize : PAGE_SIZE;
  return Math.max(1, Math.ceil(result.total / pageSize));
}

/**
 * The filter a search box text stands for. Only an entirely blank text, by the server's
 * own whitespace test (isBlankQuery), is no filter; any other text is the filter exactly
 * as typed, edge whitespace included, because the server matches it untrimmed and
 * trimming would broaden it.
 */
function filterText(text: string): string {
  return isBlankQuery(text) ? '' : text;
}

function countText(total: number): string {
  return total === 1 ? '1 record' : `${total} records`;
}

function isConnectionFailure(error: ApiError): boolean {
  return error.kind === 'unavailable' || error.kind === 'network';
}

// A field-level 400 is about the list's own query parameters, never a form field, so
// describeError's "see the messages beside each field" does not apply: such a 400 states
// the list's fixed text, with the messages split by splitListErrors.
function listErrorText(error: ApiError): string {
  switch (error.kind) {
    case 'badRequest':
    case 'server':
      return describeError(error);
    default:
      return LIST_FAILED_TEXT;
  }
}

/**
 * Splits a 400's `errors` map, keyed by query parameter. Messages about `q` belong beside
 * the search box. Those about another parameter (`page`, `pageSize`, `includeDeleted`)
 * go to the banner, prefixed with the parameter, because their text does not name it.
 */
function splitListErrors(error: ApiError | null): { search: readonly string[]; other: readonly string[] } {
  const search: string[] = [];
  const other: string[] = [];
  if (error === null) {
    return { search, other };
  }
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    if (key === SEARCH_PARAMETER) {
      search.push(...messages);
    } else {
      other.push(...messages.map((message) => `${key}: ${message}`));
    }
  }
  return { search, other };
}

function sameRecordId(a: string, b: string | null | undefined): boolean {
  return typeof b === 'string' && a.toLowerCase() === b.toLowerCase();
}

interface ListErrorBannerProps {
  error: ApiError;
  /** A 400's messages about parameters other than the search box. */
  otherMessages: readonly string[];
  onRetry: () => void;
}

/** A failed list load other than an unreachable server; a bad request is not worth repeating. */
function ListErrorBanner({ error, otherMessages, onRetry }: ListErrorBannerProps) {
  const message = listErrorText(error);
  const traceId = error.traceId;
  // describeError already ends a 500's text with the trace id.
  const traceLine = traceId !== null && !message.includes(traceId) ? `Trace id: ${traceId}` : null;
  const canRetry = error.kind !== 'badRequest' && error.kind !== 'validation';

  return (
    <div className="banner banner--error" role="alert">
      <p>{message}</p>
      {otherMessages.map((line, index) => (
        <p key={`${index}-${line}`}>{line}</p>
      ))}
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

/**
 * The records page on screen. Its form keeps another form's rows off screen; its request
 * key says which request it answers, so rows of an earlier page, filter or set stay
 * visible while the next one loads but cannot be opened. Its listing key names the page
 * without the reload counters, so a reload of the same page keeps the same listing.
 */
interface ShownPage {
  key: string;
  listingKey: string;
  viewId: number;
  page: RecordPage;
}

/**
 * A load that ended without a page, tagged with the request key it answers: an error
 * response or no response, `notLoaded` for an abort the grid did not cause, or
 * `formGone` for a 404, which reports that the form itself no longer exists.
 */
type ListFailure =
  | { key: string; kind: 'error'; error: ApiError }
  | { key: string; kind: 'notLoaded' }
  | { key: string; kind: 'formGone' };

/** The sides of the table's scroll box past which columns lie out of view. */
interface HiddenSides {
  readonly start: boolean;
  readonly end: boolean;
}

const NO_HIDDEN_SIDES: HiddenSides = { start: false, end: false };

/** Which sides of `box` hide part of its content; a sub-pixel remainder hides nothing. */
function hiddenSidesOf(box: HTMLElement): HiddenSides {
  const hiddenAtEnd = box.scrollWidth - box.clientWidth - box.scrollLeft;
  return { start: box.scrollLeft >= 1, end: hiddenAtEnd >= 1 };
}

/**
 * The searchable, pageable record list of the open form. It displays what
 * `GET /api/forms/{viewId}/records` returns and sends nothing else: the search box is
 * the server's case-insensitive "contains" over the grid columns, debounced, and the
 * server also chooses the columns. Activating a row asks the caller to open that
 * record, which runs the editor's leave flow before anything changes.
 *
 * Memoised: the workspace re-renders on every edit of the draft, and with stable
 * callbacks those renders pass the grid the same props, so up to a page of rows is not
 * rebuilt for each keystroke in a field.
 */
export const RecordGrid = memo(function RecordGrid({
  client,
  viewId,
  definition,
  includeDeleted,
  onIncludeDeletedChange,
  listVersion,
  openRecordId = null,
  onOpen,
  onFormNotFound,
  disabled = false,
}: RecordGridProps) {
  const searchId = useId();
  // Read when a load answers, so a new callback never restarts the load effect.
  const onFormNotFoundRef = useRef(onFormNotFound);
  useLayoutEffect(() => {
    onFormNotFoundRef.current = onFormNotFound;
  });
  const [q, setQ] = useState('');
  const [debouncedQ, setDebouncedQ] = useState('');
  const [page, setPage] = useState(1);
  const [shown, setShown] = useState<ShownPage | null>(null);
  const [failure, setFailure] = useState<ListFailure | null>(null);
  const [loading, setLoading] = useState(true);
  const [reloadToken, setReloadToken] = useState(0);
  // The "Show deleted" state asked for and not yet applied; `null` when none is pending.
  const [requestedIncludeDeleted, setRequestedIncludeDeleted] = useState<boolean | null>(null);
  // The table's scroll box, and which of its sides hide columns, for its scroll cue.
  const scrollRef = useRef<HTMLDivElement>(null);
  const [hiddenSides, setHiddenSides] = useState<HiddenSides>(NO_HIDDEN_SIDES);

  const searchText = filterText(debouncedQ);

  // A new filter or form starts again at page 1. Adjusted during render rather than in
  // an effect, so the load effect never requests the old page under the new filter.
  const filterKey = JSON.stringify([viewId, includeDeleted, searchText]);
  const [pageFilterKey, setPageFilterKey] = useState(filterKey);
  if (pageFilterKey !== filterKey) {
    setPageFilterKey(filterKey);
    setPage(1);
  }
  // The page of which listing is asked for: the form, the set, the filter and the page.
  const listingKey = JSON.stringify([viewId, includeDeleted, searchText, page]);
  // Every input of the list request: the page or failure on screen answers the request
  // with this key, or an earlier one.
  const requestKey = JSON.stringify([viewId, includeDeleted, searchText, page, listVersion, reloadToken]);

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
            const formGone = outcome.error.kind === 'notFound';
            setFailure(
              formGone
                ? { key: requestKey, kind: 'formGone' }
                : { key: requestKey, kind: 'error', error: outcome.error },
            );
            setLoading(false);
            if (formGone) {
              // Not a list failure to retry: the form is gone, and the workspace's
              // form-not-found handling takes over.
              onFormNotFoundRef.current();
            }
            return;
          }
          // The list shrank under a later page (a delete, a narrower filter): show its
          // last page instead of an empty one. The page only decreases, so this settles.
          const lastPage = pageCountOf(outcome.data);
          if (outcome.data.items.length === 0 && page > lastPage) {
            setPage(lastPage);
            return;
          }
          setShown({ key: requestKey, listingKey, viewId, page: outcome.data });
          setFailure(null);
          setLoading(false);
        },
        (e: unknown) => {
          // The grid's own abort: a newer request superseded this one, or it unmounted.
          if (controller.signal.aborted) {
            return;
          }
          // The client settles every failure but an abort into a result. An abort the grid
          // did not cause failed nothing, so the grid settles idle and offers the GET again;
          // any other rejection means no response was confirmed, and Retry repeats it safely.
          setFailure(
            isAbortError(e)
              ? { key: requestKey, kind: 'notLoaded' }
              : { key: requestKey, kind: 'error', error: networkError() },
          );
          setLoading(false);
        },
      );

    return () => controller.abort();
    // requestKey also carries listVersion and reloadToken, whose changes reload the page.
  }, [client, requestKey, listingKey, viewId, searchText, includeDeleted, page]);

  // The scroll box stays mounted while the next page loads, so a page of another listing
  // would open at the old sideways offset, on a blank band past its narrower columns: it
  // starts at the inline start instead. A reload of the same listing, after a save,
  // keeps the offset the user scrolled to.
  const shownListingKey = shown?.listingKey ?? null;
  useLayoutEffect(() => {
    if (scrollRef.current !== null) {
      scrollRef.current.scrollLeft = 0;
    }
  }, [shownListingKey]);

  // The grid ends the page, so a list that shrinks while the user types a search or
  // switches "Show deleted" shrinks the document under the viewport: the browser clamps
  // the window's scroll offset, and the control in use jumps down the screen. While one
  // of those controls has focus and was in view, the section keeps the height the
  // window's offset needs, and the window keeps its offset; the next change of the list
  // releases that hold first. Any other change, paging from the footer among them,
  // keeps the browser's clamp, which keeps the footer under the pointer.
  const sectionRef = useRef<HTMLElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const toggleRef = useRef<HTMLInputElement>(null);
  // The window's offset as of its last scroll event: a clamp's own event follows the
  // commit that caused it, so during that commit this is still the offset before it.
  const scrollYRef = useRef(0);
  useEffect(() => {
    const remember = () => {
      scrollYRef.current = window.scrollY;
    };
    remember();
    window.addEventListener('scroll', remember, { passive: true });
    return () => window.removeEventListener('scroll', remember);
  }, []);
  useLayoutEffect(() => {
    const section = sectionRef.current;
    if (section === null) {
      return;
    }
    // React renders the section with no style prop, so the hold is the only inline
    // style it carries, and no render overwrites or clears it.
    section.style.minBlockSize = '';
    const control = document.activeElement;
    if (control === null || (control !== searchRef.current && control !== toggleRef.current)) {
      return;
    }
    const remembered = scrollYRef.current;
    const shortfall = remembered - window.scrollY;
    if (shortfall <= 0) {
      return;
    }
    // The controls sit above the rows, in a section that packs its rows at its start, so
    // their place in the document did not move: the clamp moved them down the viewport
    // by the shortfall.
    const box = control.getBoundingClientRect();
    const topBefore = box.top - shortfall;
    if (topBefore >= window.innerHeight || topBefore + box.height <= 0) {
      return;
    }
    // box-sizing is border-box, so the section's border box plus the shortfall is the
    // height that lets the window scroll back to where it was.
    section.style.minBlockSize = `${section.getBoundingClientRect().height + shortfall}px`;
    window.scrollTo({ left: window.scrollX, top: remembered, behavior: 'instant' });
  }, [shown, failure]);

  const reload = useCallback(() => setReloadToken((token) => token + 1), []);
  // A Retry button leaves with the failure it answers, so focus moves to the list's live
  // region, which stays and announces the new load, instead of dropping to the page.
  const statusRef = useRef<HTMLParagraphElement>(null);
  const retry = useCallback(() => {
    reload();
    statusRef.current?.focus();
  }, [reload]);

  const fieldsByName = useMemo((): FieldLookup => {
    const exact = new Map<string, FieldDefinition>();
    const folded = new Map<string, FieldDefinition | null>();
    for (const pageDefinition of definition.pages) {
      for (const field of pageDefinition.fields) {
        if (exact.has(field.name)) {
          continue;
        }
        exact.set(field.name, field);
        // A second spelling of the same lower-cased name makes it ambiguous.
        const key = field.name.toLowerCase();
        folded.set(key, folded.has(key) ? null : field);
      }
    }
    return { exact, folded };
  }, [definition]);

  // Only the current request's failure is shown; Retry or a new search replaces it at once.
  const failed = failure !== null && failure.key === requestKey ? failure : null;
  const error = failed?.kind === 'error' ? failed.error : null;
  // The last page stays on screen while a newer request loads, so the list does not
  // flicker, but only the page that answers the current request opens a record: rows of
  // another page or filter, or a deleted row after "Show deleted" is switched off, never do.
  const result = failed === null && shown !== null && shown.viewId === viewId ? shown.page : null;
  // A pending "Show deleted" change is about to replace the set, so it also holds the rows.
  const togglePending = requestedIncludeDeleted !== null;
  const settled = shown !== null && shown.key === requestKey && !loading && !togglePending;
  const rowsInert = disabled || !settled;
  const items: readonly RecordSummary[] = result?.items ?? [];
  const columns = useMemo(() => {
    const first = items[0];
    return first === undefined ? [] : Object.keys(first.values);
  }, [items]);
  const lastPage = result === null ? 1 : pageCountOf(result);

  const open = (globalRecordId: string) => {
    if (!rowsInert) {
      onOpen(globalRecordId);
    }
  };

  // The row's own click would open the same record a second time.
  const handleOpenClick = (event: MouseEvent<HTMLButtonElement>, globalRecordId: string) => {
    event.stopPropagation();
    open(globalRecordId);
  };

  // The navigator re-reads the set before the toggle follows, and may first save a dirty
  // record; the requested state shows as pending meanwhile, and a further change is
  // ignored until the first one settles, whether it applied or not.
  const changeIncludeDeleted = (value: boolean) => {
    if (togglePending) {
      return;
    }
    setRequestedIncludeDeleted(value);
    const settle = () => setRequestedIncludeDeleted(null);
    void onIncludeDeletedChange(value).then(settle, settle);
  };

  const handleSearchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    // Enter applies the search at once instead of after the pause.
    if (event.key === 'Enter') {
      setDebouncedQ(q);
    }
  };

  const { search: searchErrors, other: otherErrors } = splitListErrors(error);
  // Messages about the search text belong to the text that was searched for: once the
  // box holds other text they are stale, and the next search answers for the new text.
  const searchMessages = filterText(q) === searchText ? searchErrors : [];
  const searchInvalid = searchMessages.length > 0;
  const searchErrorId = `${searchId}-error`;

  // A load that follows a failure replaces the banner with the loading text, so Retry
  // and a new search visibly run, and a repeated failure announces its alert again.
  // Retry stays available while the grid is disabled: it only repeats the list GET.
  // A 400 whose messages all concern the search box shows them there and no banner.
  let banner: ReactNode = null;
  if (error !== null && !loading) {
    if (isConnectionFailure(error)) {
      banner = <ConnectionError error={error} method="GET" onRetry={retry} />;
    } else if (error.kind !== 'validation' || otherErrors.length > 0) {
      banner = <ListErrorBanner error={error} otherMessages={otherErrors} onRetry={retry} />;
    }
  }

  // The one live region's text: loading, the outcome of the current request and the
  // count. An error response announces itself through its own alert instead.
  let statusText = '';
  if (failed === null || loading) {
    statusText =
      settled && result !== null ? (items.length === 0 ? EMPTY_TEXT : countText(result.total)) : LOADING_TEXT;
  } else if (failed.kind === 'formGone') {
    statusText = FORM_NOT_FOUND_TEXT;
  } else if (failed.kind === 'notLoaded') {
    statusText = NOT_LOADED_TEXT;
  }
  const notLoaded = failed?.kind === 'notLoaded' && !loading;

  let table: ReactNode = null;
  if (result !== null && items.length > 0) {
    const hasColumns = columns.length > 0;
    table = (
      <table aria-busy={!settled} className={settled ? undefined : 'record-grid__table--pending'}>
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
            const [firstColumn, ...otherColumns] = hasColumns ? columns : [];
            const firstText = firstColumn === undefined ? row.globalRecordId : cellText(row.values[firstColumn]);
            // A blank first cell names its record by the row's first nonblank cell, else by id.
            const nameText = nameTextOf(
              firstText !== ''
                ? firstText
                : (otherColumns.map((key) => cellText(row.values[key])).find((text) => text !== '') ??
                    row.globalRecordId),
            );
            // A pointer click anywhere on the row opens the record; the first cell's button
            // is the row's one tab stop and its named Open action. aria-disabled, unlike
            // disabled, keeps the button focused while the rows wait for the current page.
            return (
              <tr
                key={row.globalRecordId}
                className={deleted ? 'record-grid__row--deleted' : undefined}
                aria-current={sameRecordId(row.globalRecordId, openRecordId) ? 'true' : undefined}
                aria-disabled={rowsInert ? 'true' : undefined}
                onClick={() => open(row.globalRecordId)}
              >
                {/* Each nonblank cell's title holds its full value, which its column may cut short. */}
                <td title={firstText === '' ? undefined : firstText}>
                  <button
                    type="button"
                    className="record-grid__open"
                    aria-disabled={rowsInert ? 'true' : undefined}
                    onClick={(event) => handleOpenClick(event, row.globalRecordId)}
                  >
                    <span className="visually-hidden">{OPEN_RECORD_PREFIX}</span>
                    {firstText === '' ? (
                      <>
                        {/* Shown so the row has a visible open action; the name already starts with it. */}
                        <span className="record-grid__open-text" aria-hidden="true">
                          {OPEN_FALLBACK_TEXT}
                        </span>
                        <span className="visually-hidden">{nameText}</span>
                      </>
                    ) : (
                      <span className="record-grid__open-text">{nameText}</span>
                    )}
                    {deleted && <span className="visually-hidden"> (deleted)</span>}
                  </button>
                </td>
                {otherColumns.map((key) => {
                  const text = cellText(row.values[key]);
                  return (
                    <td key={key} title={text === '' ? undefined : text}>
                      {text}
                    </td>
                  );
                })}
              </tr>
            );
          })}
        </tbody>
      </table>
    );
  }

  // Measured before paint, and again whenever the box scrolls or it or its table changes
  // size. Without ResizeObserver (jsdom) the box keeps its first measurement.
  const showsTable = table !== null;
  useLayoutEffect(() => {
    const box = scrollRef.current;
    if (!showsTable || box === null) {
      return;
    }
    const measure = () => {
      const next = hiddenSidesOf(box);
      setHiddenSides((current) => (current.start === next.start && current.end === next.end ? current : next));
    };
    measure();
    box.addEventListener('scroll', measure, { passive: true });
    const observer = typeof ResizeObserver === 'function' ? new ResizeObserver(measure) : null;
    if (observer !== null) {
      observer.observe(box);
      if (box.firstElementChild !== null) {
        observer.observe(box.firstElementChild);
      }
    }
    return () => {
      box.removeEventListener('scroll', measure);
      observer?.disconnect();
    };
  }, [showsTable]);
  const frameClassName = [
    'record-grid__frame',
    ...(hiddenSides.start ? ['record-grid__frame--more-start'] : []),
    ...(hiddenSides.end ? ['record-grid__frame--more-end'] : []),
  ].join(' ');

  return (
    <section ref={sectionRef} className="record-grid" aria-label="Records">
      <label htmlFor={searchId}>Search records</label>
      <input
        ref={searchRef}
        id={searchId}
        type="search"
        autoComplete="off"
        value={q}
        disabled={disabled}
        aria-invalid={searchInvalid ? true : undefined}
        aria-describedby={searchInvalid ? searchErrorId : undefined}
        onChange={(event) => setQ(event.target.value)}
        onKeyDown={handleSearchKeyDown}
      />
      <label className={togglePending ? 'record-grid__toggle record-grid__toggle--pending' : 'record-grid__toggle'}>
        <input
          ref={toggleRef}
          type="checkbox"
          checked={requestedIncludeDeleted ?? includeDeleted}
          disabled={disabled}
          aria-busy={togglePending ? true : undefined}
          onChange={(event) => changeIncludeDeleted(event.target.checked)}
        />
        Show deleted
      </label>
      {/* Always rendered, so loading, the outcome and the count are announced; it takes
          focus from a Retry button that the new load removes. It follows the controls
          whose results it reports, so a search or a toggle shows its outcome beside them
          rather than below a page of rows. */}
      <p
        ref={statusRef}
        role="status"
        tabIndex={-1}
        className={statusText === '' ? 'record-grid__status visually-hidden' : 'record-grid__status'}
      >
        {statusText}
      </p>
      {notLoaded && (
        <button type="button" onClick={retry}>
          {RETRY_TEXT}
        </button>
      )}
      {/* After the controls and the status, so the message takes its own full row below them. */}
      {searchInvalid && (
        <div className="field-error" id={searchErrorId} role="alert">
          {searchMessages.map((message, index) => (
            <p key={`${index}-${message}`}>{message}</p>
          ))}
        </div>
      )}
      {banner}
      {/* Only the table scrolls sideways, so the search box, toggle and paging stay in view.
          The frame shades each side past which columns lie out of view. */}
      {table !== null && (
        <div className={frameClassName}>
          {/* A named, focusable region, so a keyboard can scroll the table sideways. */}
          <div
            ref={scrollRef}
            className="record-grid__scroll"
            role="region"
            aria-label={SCROLL_REGION_LABEL}
            tabIndex={0}
          >
            {table}
          </div>
        </div>
      )}
      {/* Rendered with the paging only: an empty footer would add a row and a gap. */}
      {table !== null && result !== null && (
        <footer>
          <nav className="record-grid__paging" aria-label="Record list pages">
            {/* The requested page; its page count is known again once that page arrives. */}
            <p>{settled ? `Page ${page} of ${lastPage}` : `Page ${page}`}</p>
            {/* One group, so the two buttons wrap as a pair and keep their reading order. */}
            <div className="record-grid__paging-actions">
              {/* Pending: aria-disabled rather than disabled, so a pressed button keeps focus. */}
              <button
                type="button"
                disabled={disabled || page <= 1}
                aria-disabled={settled ? undefined : 'true'}
                onClick={() => {
                  if (settled) {
                    setPage((current) => Math.max(1, current - 1));
                  }
                }}
              >
                {EARLIER_TEXT}
              </button>
              <button
                type="button"
                disabled={disabled || page >= lastPage}
                aria-disabled={settled ? undefined : 'true'}
                onClick={() => {
                  if (settled) {
                    setPage((current) => Math.min(lastPage, current + 1));
                  }
                }}
              >
                {LATER_TEXT}
              </button>
            </div>
          </nav>
        </footer>
      )}
    </section>
  );
});
