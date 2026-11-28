import { useMemo, useRef, type KeyboardEvent, type ReactNode } from 'react';
import type { PageDefinition } from '../api/types';

/** Id of a page's tab button, which the page's tab panel names in `aria-labelledby`. */
export function pageTabId(pageId: number): string {
  return `page-tab-${pageId}`;
}

/** Id of a page's tab panel, which the page's tab names in `aria-controls`. */
export function pageTabPanelId(pageId: number): string {
  return `page-panel-${pageId}`;
}

export interface PageTabsProps {
  /** The form's pages in any order; they are shown by `position`, then `pageId`. */
  pages: readonly PageDefinition[];
  /** The selected page; `null`, or an id not among `pages`, selects the first page by position. */
  selectedPageId: number | null;
  /** Called with the page a click or an arrow, Home or End key selects. */
  onSelect: (pageId: number) => void;
  /** Content of a page's panel. Called for every page, because every panel stays mounted. */
  renderPanel: (page: PageDefinition) => ReactNode;
  /** Accessible name of the tablist. */
  label?: string;
}

const DEFAULT_LABEL = 'Pages';
const NO_PAGES_TEXT = 'This form has no pages.';

function byPosition(a: PageDefinition, b: PageDefinition): number {
  return a.position - b.position || a.pageId - b.pageId;
}

/** A tab must have an accessible name; a blank metadata name falls back to its 1-based place. */
function tabText(page: PageDefinition, index: number): string {
  return page.name.trim() === '' ? `Page ${index + 1}` : page.name;
}

/**
 * One ARIA tab per page, in `position` order (WAI-ARIA tabs pattern with
 * automatic activation): ArrowRight and ArrowLeft move to the next and previous
 * tab, wrapping, Home and End to the first and last, and each move selects the
 * page and focuses its tab. Only the selected tab is in the Tab sequence.
 *
 * Every panel stays mounted and the inactive ones carry the native `hidden`
 * attribute, so each field's control exists exactly once across pages and the
 * editor's controlled values never remount when the page changes.
 *
 * A form with no pages gets no tablist and no panel, since a tablist without
 * tabs names nothing; a status says the form has no pages instead.
 */
export function PageTabs({
  pages,
  selectedPageId,
  onSelect,
  renderPanel,
  label = DEFAULT_LABEL,
}: PageTabsProps) {
  const orderedPages = useMemo(() => [...pages].sort(byPosition), [pages]);
  const tabRefs = useRef(new Map<number, HTMLButtonElement>());

  if (orderedPages.length === 0) {
    return (
      <div className="page-tabs">
        <div className="banner banner--info" role="status">
          <p>{NO_PAGES_TEXT}</p>
        </div>
      </div>
    );
  }

  const selectedId =
    orderedPages.find((page) => page.pageId === selectedPageId)?.pageId ??
    orderedPages[0]?.pageId ??
    null;

  const handleTabKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    // Modified arrows belong to the browser and assistive technology (Alt+ArrowLeft is Back).
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const lastIndex = orderedPages.length - 1;
    let targetIndex: number;
    switch (event.key) {
      case 'ArrowRight':
        targetIndex = index === lastIndex ? 0 : index + 1;
        break;
      case 'ArrowLeft':
        targetIndex = index === 0 ? lastIndex : index - 1;
        break;
      case 'Home':
        targetIndex = 0;
        break;
      case 'End':
        targetIndex = lastIndex;
        break;
      default:
        return;
    }
    // Home and End would otherwise scroll the page.
    event.preventDefault();
    const target = orderedPages[targetIndex];
    onSelect(target.pageId);
    tabRefs.current.get(target.pageId)?.focus();
  };

  return (
    <div className="page-tabs">
      <div role="tablist" aria-label={label}>
        {orderedPages.map((page, index) => {
          const selected = page.pageId === selectedId;
          const text = tabText(page, index);
          // data-text feeds a hidden bold copy (styles.css) that holds every tab at its
          // selected width, so selecting a tab moves none of its neighbours.
          return (
            <button
              key={page.pageId}
              ref={(element) => {
                if (element === null) {
                  tabRefs.current.delete(page.pageId);
                } else {
                  tabRefs.current.set(page.pageId, element);
                }
              }}
              type="button"
              role="tab"
              id={pageTabId(page.pageId)}
              aria-selected={selected}
              aria-controls={pageTabPanelId(page.pageId)}
              tabIndex={selected ? 0 : -1}
              data-text={text}
              onClick={() => onSelect(page.pageId)}
              onKeyDown={(event) => handleTabKeyDown(event, index)}
            >
              {text}
            </button>
          );
        })}
      </div>
      {orderedPages.map((page) => (
        <div
          key={page.pageId}
          role="tabpanel"
          id={pageTabPanelId(page.pageId)}
          aria-labelledby={pageTabId(page.pageId)}
          tabIndex={0}
          hidden={page.pageId !== selectedId}
        >
          {renderPanel(page)}
        </div>
      ))}
    </div>
  );
}
