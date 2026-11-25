// Fails `npm run lint` when the committed OpenAPI document (src/EpiInfo.Api/openapi/EpiInfo.Api.json) or the client
// types generated from it (web/src/api/schema.ts) no longer match what the API produces.
//
// Run by `npm run lint`, after `eslint .`. Both files are regenerated into a temporary directory by the same
// generateFresh() that `npm run api:generate` uses, then compared with the committed pair byte for byte, line endings
// aside. Requires the .NET SDK 10.0.1xx (already a build prerequisite, see the root README.md) and `npm ci` in web/.
// No database is needed and no connection string is read or printed. Nothing in the checkout is written apart from
// git-ignored build output, and the temporary directory is removed on every path.
//
// Remedy for a stale pair: npm run api:generate, then commit both files.

import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { URL, fileURLToPath } from 'node:url';

import { COMMITTED, generateFresh, removeFresh } from './api-generate.mjs';

const STALE_MESSAGE = 'OpenAPI document or generated client is stale; run npm run api:generate';

// Resolved from this file, never from process.cwd(), so reported paths are repository-relative from any directory.
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));

/** Regenerates the pair, compares it with the committed one, reports the outcome and returns the exit code. */
async function checkFreshness() {
  let fresh;
  try {
    fresh = await generateFresh();
  } catch (error) {
    // A missing SDK or a broken build is not staleness, so the stale message is not printed for it.
    process.stderr.write(`OpenAPI freshness check could not generate the document: ${errorMessage(error)}\n`);
    return failureExitCode(error);
  }

  let stale = null;
  let cleanedUp = false;
  try {
    stale = await findStale(fresh);
  } catch (error) {
    process.stderr.write(`OpenAPI freshness check could not compare the documents: ${errorMessage(error)}\n`);
  } finally {
    try {
      // Also releases the checkout's generation lock, which generateFresh() holds until now.
      await removeFresh(fresh.dir);
      cleanedUp = true;
    } catch (error) {
      process.stderr.write(`OpenAPI freshness check could not clean up ${fresh.dir}: ${errorMessage(error)}\n`);
    }
  }

  if (stale === null) {
    return 1;
  }
  if (stale.length > 0) {
    for (const file of stale) {
      process.stderr.write(`stale: ${toRepoRelative(file)}\n`);
    }
    process.stderr.write(`${STALE_MESSAGE}\n`);
    return 1;
  }
  // A run that leaves its temporary directory or the generation lock behind fails, even with a current pair.
  if (!cleanedUp) {
    return 1;
  }
  process.stdout.write('OpenAPI document and generated client are current.\n');
  return 0;
}

/** Returns the committed files that differ from, or are missing beside, their fresh counterparts. Both are checked. */
async function findStale(fresh) {
  const pairs = [
    { generated: fresh.json, committed: COMMITTED.json },
    { generated: fresh.ts, committed: COMMITTED.ts },
  ];
  const stale = [];
  for (const pair of pairs) {
    const generated = await readNormalised(pair.generated, `the generated ${path.basename(pair.generated)}`);
    const committed = await readCommitted(pair.committed);
    if (committed === null || committed !== generated) {
      stale.push(pair.committed);
    }
  }
  return stale;
}

/** The committed file's normalised text, or null when it does not exist, which counts as stale. */
async function readCommitted(file) {
  try {
    return await readNormalised(file, toRepoRelative(file));
  } catch (error) {
    if (error.cause?.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
}

// latin1 maps every byte to exactly one code unit, so comparing the strings compares the bytes. Only CRLF is turned
// into LF, which keeps a checkout with autocrlf current; any other difference, a BOM or trailing space included, is
// staleness.
async function readNormalised(file, description) {
  let text;
  try {
    text = await fs.readFile(file, 'latin1');
  } catch (error) {
    throw new Error(`could not read ${description} (${errorMessage(error)})`, { cause: error });
  }
  return text.replace(/\r\n/g, '\n');
}

function toRepoRelative(file) {
  return path.relative(repoRoot, file).split(path.sep).join('/');
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

// generateFresh() raises a GenerationError whose exitCode keeps the failing dotnet or openapi-typescript status. Any
// other error, or a status outside 1-255 that a process exit status cannot carry, exits 1.
function failureExitCode(error) {
  const code = error?.exitCode;
  return Number.isInteger(code) && code >= 1 && code <= 255 ? code : 1;
}

process.exitCode = await checkFreshness();
