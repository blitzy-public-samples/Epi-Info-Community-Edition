// Vitest globals are off, so @testing-library/react cannot register its own afterEach(cleanup); register it here.
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
});

// jsdom lays nothing out and has no Element.prototype.scrollIntoView, which FormView calls to
// bring an outcome that already holds focus into view. A no-op stands in, so tests can spy on it.
if (typeof Element.prototype.scrollIntoView !== 'function') {
  Element.prototype.scrollIntoView = function scrollIntoView(): void {
    // Nothing is laid out, so there is nothing to scroll.
  };
}
