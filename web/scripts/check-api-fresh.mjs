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

import { Buffer } from 'node:buffer';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { URL, fileURLToPath } from 'node:url';

import { COMMITTED, generateFresh, removeFresh } from './api-generate.mjs';

const STALE_MESSAGE = 'OpenAPI document or generated client is stale; run npm run api:generate';

// Opening an artifact never waits and never acquires a terminal: O_NONBLOCK makes the open of a FIFO return at once
// instead of waiting for a writer, and O_NOCTTY keeps a terminal device from becoming the controlling terminal.
// Windows defines neither flag and has neither kind of file. Neither changes how a regular file reads.
const openFlags = fs.constants.O_RDONLY | (fs.constants.O_NONBLOCK ?? 0) | (fs.constants.O_NOCTTY ?? 0);

// Outcomes of comparing a committed file with its fresh counterpart. NOT_REGULAR_FILE is stale as well.
const CURRENT = 'current';
const STALE = 'stale';
const NOT_REGULAR_FILE = Symbol('not a regular file');

// Resolved from this file, never from process.cwd(), so reported paths are repository-relative from any directory.
const repoRoot = fileURLToPath(new URL('../..', import.meta.url));

/** Regenerates the pair, compares it with the committed one, reports the outcome and returns the exit code. */
async function checkFreshness() {
  let fresh;
  try {
    fresh = await generateFresh();
  } catch (error) {
    // Without a fresh pair the check cannot confirm that the committed one is current, so it fails with the one
    // failure line and exit status 1 that a stale pair gets, after a line that says why generation failed.
    process.stderr.write(`OpenAPI freshness check could not generate the document: ${errorMessage(error)}\n`);
    process.stderr.write(`${STALE_MESSAGE}\n`);
    return 1;
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
    for (const { file, notRegularFile } of stale) {
      if (notRegularFile) {
        process.stderr.write(`OpenAPI freshness check: ${toRepoRelative(file)} is not a regular file\n`);
      }
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

/**
 * Returns the committed files that differ from, are missing beside, or are not regular files in place of their fresh
 * counterparts, each as { file, notRegularFile }. Both are checked.
 */
async function findStale(fresh) {
  const pairs = [
    { generated: fresh.json, committed: COMMITTED.json },
    { generated: fresh.ts, committed: COMMITTED.ts },
  ];
  const stale = [];
  for (const pair of pairs) {
    const generated = await readGenerated(pair.generated);
    const outcome = await compareCommitted(pair.committed, generated);
    if (outcome !== CURRENT) {
      stale.push({ file: pair.committed, notRegularFile: outcome === NOT_REGULAR_FILE });
    }
  }
  return stale;
}

/** The fresh file's normalised text. It is this run's own build output, so it is read whole. */
async function readGenerated(file) {
  const description = `the generated ${path.basename(file)}`;
  let bytes;
  try {
    bytes = await readRegularFile(file, (handle) => handle.readFile());
  } catch (error) {
    throw new Error(`could not read ${description} (${errorMessage(error)})`, { cause: error });
  }
  if (bytes === NOT_REGULAR_FILE) {
    throw new Error(`could not read ${description}: it is not a regular file`);
  }
  return normalise(bytes);
}

/**
 * Compares the committed file with the fresh normalised text and returns CURRENT, STALE (different or missing) or
 * NOT_REGULAR_FILE. Normalising removes one byte per CRLF, and each CRLF becomes one of the fresh text's n line feeds,
 * so a committed file that matches its L characters holds between L and L + n bytes. A file outside that range is
 * stale without being read. One inside it is read up to one byte past the range, so a file that grew since its size
 * was taken is still too long to match.
 */
async function compareCommitted(file, generated) {
  const minimum = generated.length;
  const maximum = minimum + countLineFeeds(generated);
  try {
    return await readRegularFile(file, async (handle, size) => {
      if (size < minimum || size > maximum) {
        return STALE;
      }
      const bytes = await readAtMost(handle, maximum + 1);
      return bytes.length <= maximum && normalise(bytes) === generated ? CURRENT : STALE;
    });
  } catch (error) {
    if (error.code === 'ENOENT') {
      return STALE;
    }
    throw new Error(`could not read ${toRepoRelative(file)} (${errorMessage(error)})`, { cause: error });
  }
}

/**
 * Opens the file and returns read(handle, size) when it is a regular file, or NOT_REGULAR_FILE, without reading, when
 * it is anything else. The handle is always closed. A failed open, a missing file included, is thrown.
 */
async function readRegularFile(file, read) {
  let handle;
  try {
    handle = await fs.open(file, openFlags);
  } catch (error) {
    // Some kinds of file refuse the open itself (a socket fails with ENXIO, and so can a device without a driver), so
    // an existing path that is not a regular file is reported as such whether or not it opens.
    if (error.code !== 'ENOENT' && (await isOtherThanRegularFile(file))) {
      return NOT_REGULAR_FILE;
    }
    throw error;
  }
  try {
    const stats = await handle.stat();
    return stats.isFile() ? await read(handle, stats.size) : NOT_REGULAR_FILE;
  } finally {
    await handle.close();
  }
}

/** Whether the path exists as something other than a regular file. */
async function isOtherThanRegularFile(file) {
  try {
    return !(await fs.stat(file)).isFile();
  } catch {
    return false;
  }
}

/** Reads the file from its start until it ends or `length` bytes are read, and returns the bytes read. */
async function readAtMost(handle, length) {
  const buffer = Buffer.alloc(length);
  let filled = 0;
  while (filled < length) {
    const { bytesRead } = await handle.read(buffer, filled, length - filled, filled);
    if (bytesRead === 0) {
      break;
    }
    filled += bytesRead;
  }
  return buffer.subarray(0, filled);
}

// latin1 maps every byte to exactly one code unit, so comparing the strings compares the bytes. Only CRLF is turned
// into LF, which keeps a checkout with autocrlf current; any other difference, a BOM or trailing space included, is
// staleness.
function normalise(bytes) {
  return bytes.toString('latin1').replace(/\r\n/g, '\n');
}

function countLineFeeds(text) {
  let count = 0;
  for (let index = text.indexOf('\n'); index !== -1; index = text.indexOf('\n', index + 1)) {
    count += 1;
  }
  return count;
}

function toRepoRelative(file) {
  return path.relative(repoRoot, file).split(path.sep).join('/');
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

process.exitCode = await checkFreshness();
