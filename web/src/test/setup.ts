// Vitest globals are off, so @testing-library/react cannot register its own afterEach(cleanup); register it here.
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

afterEach(() => {
  cleanup();
});
