// Vite build, development server and Vitest configuration for the web client.
//
// No plugins: Vite's built-in esbuild compiles TSX with the automatic JSX runtime,
// matching tsconfig.json "jsx": "react-jsx", so @vitejs/plugin-react is not needed.
import { defineConfig } from 'vitest/config';

// The API listens only on the IPv4 loopback address (Kestrel:Endpoints:Http:Url in
// src/EpiInfo.Api/appsettings.json). The proxy names 127.0.0.1 literally, never
// localhost, which can resolve to ::1 where nothing listens.
const apiOrigin = 'http://127.0.0.1:5000';

export default defineConfig({
  esbuild: {
    jsx: 'automatic',
  },

  // Served by the API from Web:ClientPath (../../web/dist) at the site root, so the
  // default base '/' keeps asset URLs at /assets/*.
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },

  // Development only (npm run dev). Same-origin proxying means the API needs no CORS.
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiOrigin, changeOrigin: false },
      '/health': { target: apiOrigin, changeOrigin: false },
    },
  },

  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // Vitest's default include also matches *.spec.ts, which would run the Playwright
    // smoke test e2e/smoke.spec.ts under Vitest; only src/**/*.test.* are unit tests.
    include: ['src/**/*.test.{ts,tsx}'],
    exclude: ['node_modules/**', 'dist/**', 'e2e/**'],
    globals: false,
    restoreMocks: true,
    clearMocks: true,
  },
});
