// Fails `npm run lint` when the committed OpenAPI document (src/EpiInfo.Api/openapi/EpiInfo.Api.json) or the client
// types generated from it (web/src/api/schema.ts) no longer match what the API produces.
//
// Run by `npm run lint`, after `eslint .`. Both files are regenerated into a temporary directory by the same
// generateFresh() that `npm run api:generate` uses, then compared with the committed pair byte for byte, line endings
// aside. Requires the .NET SDK 10.0.1xx (already a build prerequisite, see the root README.md) and `npm ci` in web/.
// No database is needed and no connection string is read or printed. Nothing in the checkout is written apart from
// git-ignored build output. A committed file is compared only as a regular file whose directories all belong to this
// checkout: a link in its place or among its directories fails the check (see assertInsideCheckout in
// api-generate.mjs).
//
// Removing the temporary directory and releasing the generation lock are attempted on the normal path and after an
// interruption by SIGINT, SIGTERM or SIGHUP: an interrupted check stops every process it started, with their
// descendants, before it removes the directory, then reports the interruption rather than staleness and ends by that
// signal (see the Interruption section and currentInterruption in api-generate.mjs). A removal or release that fails
// is reported on stderr on either path, and fails the check even with a current pair. A lock kept because the dotnet
// process it records has not ended is reported too, and the next run removes it once that process has ended. A
// SIGKILL of the check's own node process, which no process can catch, skips that cleanup, and a descendant that
// leaves its process group escapes the stop.
//
// Remedy for a stale pair: npm run api:generate, then commit both files.
//
// `node scripts/check-api-fresh.mjs --help` (or -h) prints the usage and checks nothing; the script takes no other
// argument, and any other argument is refused with exit status 2 before anything runs (see handleArguments in
// api-generate.mjs).

import { Buffer } from 'node:buffer';
import fs from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { URL, fileURLToPath } from 'node:url';

import {
  COMMITTED,
  CheckoutPathError,
  assertInsideCheckout,
  currentInterruption,
  formatUsage,
  generateFresh,
  handleArguments,
  removeFresh,
} from './api-generate.mjs';

const STALE_MESSAGE = 'OpenAPI document or generated client is stale; run npm run api:generate';

// Opening an artifact never waits, never acquires a terminal and never follows a symbolic link: O_NONBLOCK makes the
// open of a FIFO return at once instead of waiting for a writer, O_NOCTTY keeps a terminal device from becoming the
// controlling terminal, and O_NOFOLLOW makes the open of a link fail instead of reaching what it points to. Windows
// defines none of the three and has neither FIFOs nor terminal devices; there the path is examined after the open
// instead (see readRegularFile). None of them changes how a regular file reads.
const openFlags =
  fs.constants.O_RDONLY |
  (fs.constants.O_NONBLOCK ?? 0) |
  (fs.constants.O_NOCTTY ?? 0) |
  (fs.constants.O_NOFOLLOW ?? 0);

// Outcomes of comparing a committed file with its fresh counterpart. readRegularFile returns NOT_REGULAR_FILE for a
// path that is not a regular file, which makes a committed file stale.
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
    if (currentInterruption() !== null) {
      return reportInterrupted();
    }
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

  if (currentInterruption() !== null) {
    return reportInterrupted();
  }
  if (stale === null) {
    return 1;
  }
  if (stale.length > 0) {
    for (const { file, problem } of stale) {
      if (problem !== null) {
        process.stderr.write(`OpenAPI freshness check: ${problem}\n`);
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
 * An interrupted check has no outcome, so it is reported on one line, never as staleness or as a generation failure,
 * and returns 128 + n, the status the process also ends with when the interruption raises signal n again.
 */
function reportInterrupted() {
  const { signal, exitCode } = currentInterruption();
  process.stderr.write(`OpenAPI freshness check was interrupted by ${signal} and did not complete\n`);
  return exitCode;
}

/**
 * Returns the committed files that differ from, are missing beside, or are not regular files of this checkout in place
 * of their fresh counterparts, each as { file, problem }, where problem is null for a file that differs or is missing,
 * and otherwise the sentence that says why the file was not compared. Both are checked.
 */
async function findStale(fresh) {
  const pairs = [
    { generated: fresh.json, committed: COMMITTED.json },
    { generated: fresh.ts, committed: COMMITTED.ts },
  ];
  const stale = [];
  for (const pair of pairs) {
    const generated = await readGenerated(pair.generated);
    const { outcome, problem } = await compareCommitted(pair.committed, generated);
    if (outcome !== CURRENT) {
      stale.push({ file: pair.committed, problem });
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
 * Compares the committed file with the fresh normalised text and returns { outcome, problem }: outcome is CURRENT, or
 * STALE for a file that differs, is missing or is not compared, and problem is null, or for a file not compared, the
 * sentence that says why. The file is compared only as a regular file whose directories all belong to this checkout
 * (see assertInsideCheckout in api-generate.mjs); a missing directory is a missing file. Normalising removes one byte
 * per CRLF, and each CRLF becomes one of the fresh text's n line feeds, so a committed file that matches its L
 * characters holds between L and L + n bytes. A file outside that range is stale without being read. One inside it is
 * read up to one byte past the range, so a file that grew since its size was taken is still too long to match.
 */
async function compareCommitted(file, generated) {
  const minimum = generated.length;
  const maximum = minimum + countLineFeeds(generated);
  let outcome;
  try {
    await assertInsideCheckout(file);
    outcome = await readRegularFile(file, async (handle, size) => {
      if (size < minimum || size > maximum) {
        return STALE;
      }
      const bytes = await readAtMost(handle, maximum + 1);
      return bytes.length <= maximum && normalise(bytes) === generated ? CURRENT : STALE;
    });
  } catch (error) {
    if (error.code === 'ENOENT') {
      return { outcome: STALE, problem: null };
    }
    if (error instanceof CheckoutPathError) {
      return { outcome: STALE, problem: error.message };
    }
    throw new Error(`could not read ${toRepoRelative(file)} (${errorMessage(error)})`, { cause: error });
  }
  if (outcome === NOT_REGULAR_FILE) {
    return { outcome: STALE, problem: `${toRepoRelative(file)} is not a regular file` };
  }
  return { outcome, problem: null };
}

/**
 * Opens the file and returns read(handle, size) when it is a regular file, or NOT_REGULAR_FILE, without reading, when
 * it is anything else, a symbolic link included, whatever it points to. The handle is always closed. A failed open, a
 * missing file included, is thrown.
 */
async function readRegularFile(file, read) {
  let handle;
  try {
    handle = await fs.open(file, openFlags);
  } catch (error) {
    // Some kinds of file refuse the open itself (a socket fails with ENXIO, as can a device without a driver, and a
    // symbolic link fails with ELOOP, or EMLINK on some BSDs), so an existing path that is not a regular file is
    // reported as such whether or not it opens.
    if (error.code !== 'ENOENT' && (await isOtherThanRegularFile(file))) {
      return NOT_REGULAR_FILE;
    }
    throw error;
  }
  try {
    const stats = await handle.stat();
    if (!stats.isFile() || !(await isOpenedPath(file, stats))) {
      return NOT_REGULAR_FILE;
    }
    return await read(handle, stats.size);
  } finally {
    await handle.close();
  }
}

/**
 * Whether the path itself is the regular file that was opened. Where O_NOFOLLOW is defined, the open has already
 * refused a symbolic link in the path's place. Where it is not (Windows), the open may have followed one, so the path
 * must be, without following links, a regular file with the opened file's device and inode.
 */
async function isOpenedPath(file, opened) {
  if (fs.constants.O_NOFOLLOW !== undefined) {
    return true;
  }
  const stats = await fs.lstat(file);
  return stats.isFile() && stats.dev === opened.dev && stats.ino === opened.ino;
}

/** Whether the path exists as something other than a regular file, a symbolic link included. */
async function isOtherThanRegularFile(file) {
  try {
    return !(await fs.lstat(file)).isFile();
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

const usage = () =>
  formatUsage({
    synopsis: [
      'Usage: node scripts/check-api-fresh.mjs [--help | -h]',
      '   or: npm run lint   (from web/, after eslint .)',
    ],
    description:
      `Checks that the committed OpenAPI document ${toRepoRelative(COMMITTED.json)} and the client types ` +
      `generated from it, ${toRepoRelative(COMMITTED.ts)}, are current. It regenerates both into a new temporary ` +
      'directory exactly as npm run api:generate does, and compares them with the committed pair byte for byte, ' +
      'line endings aside. It writes nothing in the checkout apart from git-ignored build output, and works from ' +
      'any directory. For a stale pair, run npm run api:generate, then commit both files.',
    exitStatuses: [
      ['0', 'both committed files are current'],
      ['1', 'a committed file is stale, or the check could not generate or compare the pair'],
    ],
    interrupted:
      'removing the temporary directory and releasing the lock are attempted, a failure is reported, and nothing ' +
      'is reported as stale',
  });

// The arguments are read before anything else, so --help and a refused argument check nothing.
process.exitCode = handleArguments(process.argv.slice(2), usage()) ?? (await checkFreshness());
