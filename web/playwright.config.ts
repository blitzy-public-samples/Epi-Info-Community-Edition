// Playwright configuration for the browser smoke test (e2e/smoke.spec.ts).
//
// The test runs against the real API serving the built client in web/dist. Without E2E_BASE_URL, Playwright
// starts that API itself and waits for /health to answer 200, so a database the API cannot reach fails the run
// instead of skipping it. With E2E_BASE_URL, the named API is tested and no server is started.
import { defineConfig, devices } from '@playwright/test';

import { resolveConnectionString } from './e2e/support/connection';

// Names the variable and the requirement only: the value may be anything, so it is never echoed.
const EXTERNAL_BASE_URL_REQUIREMENT =
  'E2E_BASE_URL must be unset or an absolute URL that starts with http:// or https:// followed by a host, such as ' +
  'http://127.0.0.1:5000, with no user name, password, control character or backslash.';

// The scheme, '//' and a non-empty authority, which ends at the first '/', '?' or '#'.
const HTTP_URL_PREFIX = /^https?:\/\/([^/?#]+)/i;

function hasControlCharacterOrBackslash(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code <= 0x1f || code === 0x7f || code === 0x5c) {
      return true;
    }
  }
  return false;
}

// Checked as this file loads, so a value that names no API fails the run before the smoke test takes its database
// lock, instead of switching off the managed server and failing at the first request. The URL parser repairs what it
// is given: it drops tabs and newlines, reads a backslash as '/', and supplies or skips the slashes after the scheme.
// So the text itself must already be the URL before the parser confirms it. The parser's own error is not attached:
// it would carry the value.
function checkExternalBaseUrl(value: string): string {
  const authority = HTTP_URL_PREFIX.exec(value)?.[1];
  // The API has no authentication, so a user name or password in the authority has no use there, and in baseURL it
  // would put a secret into every report and trace that shows the configuration.
  if (
    authority === undefined ||
    authority.includes('@') ||
    hasControlCharacterOrBackslash(value) ||
    !URL.canParse(value)
  ) {
    throw new Error(EXTERNAL_BASE_URL_REQUIREMENT);
  }
  const url = new URL(value);
  if ((url.protocol !== 'http:' && url.protocol !== 'https:') || url.hostname === '') {
    throw new Error(EXTERNAL_BASE_URL_REQUIREMENT);
  }
  return value;
}

// Blank counts as unset, as the smoke test reads it, so the two never disagree on whether the API is external.
const configuredBaseUrl = process.env.E2E_BASE_URL?.trim() || undefined;
const externalBaseUrl = configuredBaseUrl === undefined ? undefined : checkExternalBaseUrl(configuredBaseUrl);

// The API listens on IPv4 loopback only; the name localhost reaches it through Chromium's and Node's
// address-family fallback from ::1 to 127.0.0.1.
const baseURL = externalBaseUrl ?? 'http://localhost:5000';

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  outputDir: 'test-results',

  // One worker and no retries: the smoke test holds the database-wide test lock, and a pass must be a first pass.
  workers: 1,
  retries: 0,
  fullyParallel: false,
  forbidOnly: true,

  // The smoke test walks the whole data-entry workflow and sets its own longer limit for the lock wait.
  timeout: 120_000,

  reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],

  use: {
    baseURL,
    trace: 'retain-on-failure',
  },

  // Chromium only: npm run e2e installs no other browser.
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: externalBaseUrl
    ? undefined
    : {
        // Runs from web/. dotnet run makes src/EpiInfo.Api the content root, so Web:ClientPath ../../web/dist
        // resolves to the npm run build output. --no-build relies on dotnet build EpiInfo.Web.sln having run;
        // --no-launch-profile keeps a local launchSettings.json from supplying a listen URL the binding guard
        // rejects. No listen setting is passed: the committed http://127.0.0.1:5000 endpoint is the only one.
        command: 'dotnet run --project ../src/EpiInfo.Api/EpiInfo.Api.csproj --no-build --no-launch-profile',
        // /health answers 503 while SQL Server is unreachable, so an unreachable database times the run out.
        url: 'http://127.0.0.1:5000/health',
        timeout: 120_000,
        // A server already on port 5000 is never tested silently; testing one is what E2E_BASE_URL is for.
        reuseExistingServer: false,
        // Resolved only here, so an external API run needs no database variables to load this file. The value
        // holds the password and is passed to the API process only, never printed.
        env: { ConnectionStrings__EpiInfo: resolveConnectionString() },
      },
});
