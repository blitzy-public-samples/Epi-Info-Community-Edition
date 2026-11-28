// The browser entry point: mounts the application shell into index.html's #root. There is
// no configuration to load here, unlike legacy Enter's startup: the API owns the connection
// string, and the client only calls /api and /health on its own origin.
//
// StrictMode stays on. In development it simulates an unmount and remount right after each
// component first mounts, so each effect's setup and cleanup run one extra time then, not on
// later dependency changes. The hooks must therefore cancel their own work (AbortController or
// generation checks) rather than rely on an effect running once.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import App from './App';
import './styles.css';

const rootElement = document.getElementById('root');
if (rootElement === null) {
  throw new Error('Root element #root not found');
}

createRoot(rootElement).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
