// Regenerates the committed OpenAPI document (src/EpiInfo.Api/openapi/EpiInfo.Api.json) and the client types
// generated from it (web/src/api/schema.ts).
//
// Usage, from web/ after any change to the API contract:  npm run api:generate
//
// Requires the .NET SDK 10.0.1xx (see the root README.md) and `npm ci` already run in web/. No database is needed:
// the build-time generator hosts Program without starting Kestrel. Nothing is added to the environment the build
// and the CLI inherit, so no connection string is read, set or printed here.
//
// check-api-fresh.mjs, the second half of `npm run lint`, reuses generateFresh() and compares its output with
// COMMITTED, so both scripts produce the documents in exactly the same way.

import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { readFileSync, unlinkSync } from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { setTimeout as sleep } from 'node:timers/promises';
import { URL, fileURLToPath } from 'node:url';

// Resolved from this file, never from process.cwd(), so the script works from any working directory.
const webDir = fileURLToPath(new URL('..', import.meta.url));
const repoRoot = path.resolve(webDir, '..');
const apiDir = path.join(repoRoot, 'src', 'EpiInfo.Api');
const apiProject = path.join(apiDir, 'EpiInfo.Api.csproj');
// The OpenAPI target's incremental marker, its _OpenApiDocumentsCache. Each run pins it into the run's own temporary
// directory (see generateFresh), so no marker another build owns is read or deleted. The default layout's obj/ marker,
// which a build without the pin writes, is a leftover that is removed only inside the checkout (see
// removeObjLeftoverMarker).
const openApiCacheMarkerName = 'EpiInfo.Api.OpenApiFiles.cache';
const defaultOpenApiCacheMarker = path.join(apiDir, 'obj', openApiCacheMarkerName);
const openapiTsPackageJson = path.join(webDir, 'node_modules', 'openapi-typescript', 'package.json');

// The default v1 document is named after the project; the generated client is written beside it.
const documentFileName = 'EpiInfo.Api.json';
const clientFileName = 'schema.ts';

/** The committed files that `npm run api:generate` rewrites and check-api-fresh.mjs compares. */
export const COMMITTED = Object.freeze({
  json: path.join(apiDir, 'openapi', documentFileName),
  ts: path.join(webDir, 'src', 'api', clientFileName),
});

/** A generation failure; exitCode carries the status of the failing dotnet or openapi-typescript run. */
export class GenerationError extends Error {
  constructor(message, exitCode = 1) {
    super(message);
    this.name = 'GenerationError';
    this.exitCode = Number.isInteger(exitCode) && exitCode !== 0 ? exitCode : 1;
  }
}

/**
 * Builds the API with document generation forced on, then runs openapi-typescript on the result, all inside a new
 * temporary directory. Generation is forced by pinning the OpenAPI target's incremental marker into that directory, so
 * the target finds its output missing and runs in every build layout; the only marker deleted is a regular-file
 * leftover in the checkout's own src/EpiInfo.Api/obj/. Returns { json, ts, dir }: the fresh document, the fresh client
 * types and the directory that holds them, which the caller removes with removeFresh(dir), marker included. Until then
 * the run holds the checkout's generation lock, which every other run of this checkout waits for. A failed run removes
 * the directory and releases the lock itself.
 */
export async function generateFresh() {
  // Every checkout and temporary path a later step hands to a shell or reads as a URL is checked first (see
  // assertPathCharacters), so a refused path creates nothing and starts no build. Each is checked as the text that
  // step uses: MSBuild resolves no symlinks, so the build is given the canonical checkout and document directory. The
  // values MSBuild itself evaluates into the generator command are checked before the build (see
  // assertGeneratorCommandInputs).
  const checkout = await fs.realpath(repoRoot);
  assertPathCharacters(
    checkout,
    'repository checkout',
    shellInDoubleQuotes,
    'clone the repository to a path without these characters',
  );
  const checkoutProject = path.join(checkout, path.relative(repoRoot, apiProject));

  const tempVariables = process.platform === 'win32' ? 'TMP and TEMP' : 'TMPDIR';
  const tempRemedy = `set ${tempVariables} to a directory without these characters`;
  // MSBuild keeps its own Exec scripts under the directory .NET takes from the environment, as written there and
  // resolved from the build's working directory: TMPDIR (else /tmp) on POSIX; TMP, TEMP, then USERPROFILE on Windows.
  const buildTemp =
    process.platform === 'win32'
      ? process.env.TMP || process.env.TEMP || process.env.USERPROFILE
      : process.env.TMPDIR;
  if (buildTemp) {
    if (!path.isAbsolute(buildTemp)) {
      throw new GenerationError(
        `the temporary directory ${JSON.stringify(buildTemp)} is relative, and the build resolves it from the ` +
          `repository checkout rather than from this script's working directory; set ${tempVariables} to an ` +
          'absolute directory',
      );
    }
    assertPathCharacters(buildTemp, 'temporary directory', shellUnquoted, tempRemedy);
  }
  const tempRoot = await fs.realpath(path.resolve(os.tmpdir()));
  assertPathCharacters(tempRoot, 'temporary directory', shellInDoubleQuotes, tempRemedy);
  assertPathCharacters(tempRoot, 'temporary directory', urlText, tempRemedy);

  const dir = await fs.mkdtemp(path.join(tempRoot, 'epiinfo-openapi-'));
  try {
    // Runs of one checkout share the API's obj/ and bin/ outputs, so concurrent builds would write the same files and
    // the generator could read an assembly another build is still writing. Each run holds the checkout's generation
    // lock from here until removeFresh(dir), which also covers the caller's publication or comparison of the committed
    // pair. Every dotnet process that writes those outputs is started with the lock, which records it, so a run killed
    // while its build goes on leaves a lock that stays until the build ends.
    const lock = await acquireGenerationLock(dir);

    // The generator target is incremental on its marker, _OpenApiDocumentsCache: while that file is newer than the
    // assembly, an up-to-date tree neither regenerates the document nor writes it to a new directory. The targets
    // place it in BaseIntermediateOutputPath, which a redirected or shared intermediate directory can move to where
    // other builds keep theirs. As a global property the marker is pinned into this run's new directory instead, which
    // no assignment in the targets or an imported file overrides (the evaluation below confirms it), so the target
    // finds its output missing and runs in every layout, no other build's marker is read or touched, and
    // removeFresh(dir) removes it with the directory.
    const cacheMarker = path.join(dir, openApiCacheMarkerName);
    // The restore, the evaluation and the build get the same global properties, so all three see the same project.
    // cwd is the repository root so the SDK resolves the root global.json.
    const buildProperties = [
      '-p:OpenApiGenerateDocumentsOnBuild=true',
      `-p:OpenApiDocumentsDirectory=${escapeMsbuildPropertyValue(dir)}`,
      `-p:_OpenApiDocumentsCache=${escapeMsbuildPropertyValue(cacheMarker)}`,
    ];
    // The generator command is built from evaluated MSBuild properties, which are read back and checked before the
    // build can run it. Their evaluation needs the restore's package imports, and the restore writes the shared obj/,
    // so it runs here, under the lock, as its own step.
    await runChecked(
      'dotnet',
      ['restore', checkoutProject, ...buildProperties, '--nologo'],
      checkout,
      `dotnet restore ${toRepoRelative(apiProject)}`,
      dotnetStartFailure,
      { lock },
    );
    await assertGeneratorCommandInputs(checkoutProject, buildProperties, checkout, dir, cacheMarker, lock);

    // The marker an earlier build without the pin left in the default layout's obj/ no longer decides anything, so it
    // is removed only where it is certainly this checkout's own; anything else there is left untouched.
    await removeObjLeftoverMarker(checkout);

    // The build's own restore finds nothing left to do, and the build recompiles first when sources changed, so the
    // generator never reads a stale assembly.
    await runChecked(
      'dotnet',
      ['build', checkoutProject, ...buildProperties, '--nologo'],
      checkout,
      `dotnet build ${toRepoRelative(apiProject)}`,
      dotnetStartFailure,
      { lock },
    );

    const json = path.join(dir, documentFileName);
    if (!(await isFile(json))) {
      const found = await fs.readdir(dir);
      throw new GenerationError(
        `OpenAPI generation produced no ${documentFileName} in ${dir}; found: ${found.length > 0 ? found.join(', ') : 'nothing'}`,
      );
    }
    // The generator writes its file list to the marker the target passes it, so a document without the pinned marker
    // means the installed package no longer takes its marker from _OpenApiDocumentsCache, and a warm run would skip on
    // a marker outside this directory.
    if (!(await isFile(cacheMarker))) {
      throw new GenerationError(
        `OpenAPI generation wrote no cache marker ${openApiCacheMarkerName} in ${dir}, so the installed ` +
          'Microsoft.Extensions.ApiDescription.Server no longer reads its marker from _OpenApiDocumentsCache and ' +
          'generation cannot be forced; check the package version against Directory.Packages.props',
      );
    }

    // Relative names with the temporary directory as cwd: the CLI resolves its arguments as URLs against
    // file://<cwd>/, which misreads an absolute Windows path as a URL scheme, and an empty cwd guarantees that no
    // redocly.yaml changes the output. No flags, so the result is the CLI's default output.
    const cli = await openapiTypescriptCli();
    await runChecked(
      process.execPath,
      [cli, documentFileName, '-o', clientFileName],
      dir,
      'openapi-typescript',
      (error) => `openapi-typescript could not be started (${error.code ?? error.message})`,
    );

    const ts = path.join(dir, clientFileName);
    if (!(await isFile(ts))) {
      throw new GenerationError(`openapi-typescript produced no ${clientFileName} in ${dir}`);
    }

    return { json, ts, dir };
  } catch (error) {
    await cleanUpAfter(true, dir, () => removeFresh(dir));
    throw error;
  }
}

/** Removes a directory returned by generateFresh(), and releases the generation lock its run holds. */
export async function removeFresh(dir) {
  const lock = heldLocks.get(dir);
  if (lock === undefined) {
    await fs.rm(dir, { recursive: true, force: true });
    return;
  }
  heldLocks.delete(dir);
  // Both steps are always attempted, so a directory that cannot be removed never keeps the checkout locked.
  const [removal, release] = await Promise.allSettled([
    fs.rm(dir, { recursive: true, force: true }),
    releaseGenerationLock(lock),
  ]);
  if (removal.status === 'rejected' && release.status === 'rejected') {
    throw new GenerationError(
      `could not remove ${dir} (${errorMessage(removal.reason)}), and ${errorMessage(release.reason)}`,
    );
  }
  if (removal.status === 'rejected') {
    throw removal.reason;
  }
  if (release.status === 'rejected') {
    throw release.reason;
  }
}

// Generation lock. One file per checkout, under the API's git-ignored obj/, created exclusively by the run that holds
// it and recording { pid, hostname, token, acquiredAt }, plus childPid while a dotnet process the run started is
// writing the checkout's outputs: a child outlives a killed parent, so the lock must outlive it too. A waiting run
// polls, and gives up after the wait limit. A lock is stale when its record names this host, a holder that no longer
// runs or is this very process (which holds no lock while acquiring one), and no child that still runs; a record that
// never became readable is stale once older than the grace period. A stale lock is removed (see removeStaleInstance),
// never one held from another host. Within one process, generateFresh() calls queue first, so the file is contended
// only between processes and a process never waits for itself.
const generationLock = path.join(apiDir, 'obj', 'EpiInfo.Api.OpenApiGenerate.lock');
const lockPollMilliseconds = 250;
const lockWaitLimitMilliseconds = 10 * 60 * 1000;
const lockRecordGraceMilliseconds = 30 * 1000;
// How many levels of claim files removeStaleInstance creates at most, which keeps each name within file-name limits.
const claimDepthLimit = 3;

// Settles when the latest queued generateFresh() call of this process has released its lock.
let generationQueue = Promise.resolve();
// The lock of each run still open, by its temporary directory, until removeFresh(dir) releases it.
const heldLocks = new Map();

async function acquireGenerationLock(dir) {
  const previous = generationQueue;
  let leaveQueue;
  generationQueue = new Promise((resolve) => {
    leaveQueue = resolve;
  });
  await previous;
  try {
    const record = await acquireLockFile();
    // Safety net for a process that exits without removeFresh(dir); a release in removeFresh deregisters it.
    const releaseOnExit = () => releaseLockFileSync(record.token);
    process.on('exit', releaseOnExit);
    // record is the content of the lock file, which recordLockChild rewrites while a child runs.
    const lock = { token: record.token, record, releaseOnExit, leaveQueue };
    heldLocks.set(dir, lock);
    return lock;
  } catch (error) {
    leaveQueue();
    throw error;
  }
}

async function acquireLockFile() {
  await fs.mkdir(path.dirname(generationLock), { recursive: true });
  const record = newLockRecord();
  const deadline = Date.now() + lockWaitLimitMilliseconds;
  let announced = false;
  for (;;) {
    if (await createExclusive(generationLock, record)) {
      await removeLockLeftovers();
      return record;
    }
    const holder = await readLockRecord(generationLock);
    const stale = holder !== null && isStale(holder);
    if (stale && (await removeStaleInstance(generationLock, holder, 0))) {
      continue;
    }
    if (Date.now() >= deadline) {
      throw new GenerationError(
        `timed out after ${lockWaitLimitMilliseconds / 60000} minutes waiting for the OpenAPI generation lock ` +
          `${toRepoRelative(generationLock)}, held by ${describeHolder(holder)}; if no api:generate or lint run of ` +
          'this checkout is active, delete that file and every file beside it whose name begins with its name, ' +
          'and run again',
      );
    }
    // A lock that vanished since the attempt, or a stale one that another run is removing, is simply tried again
    // after the pause, with nothing to report.
    if (!announced && holder !== null && !stale) {
      process.stderr.write(
        `waiting for the OpenAPI generation lock ${toRepoRelative(generationLock)}, held by ${describeHolder(holder)}\n`,
      );
      announced = true;
    }
    await sleep(lockPollMilliseconds);
  }
}

// Removes a stale lock, or a stale claim file, without racing other waiters. The file is removed only by the run that
// created its claim `<file>.claim-<id>` exclusively, and under that claim only after reading it again and finding it
// still the instance judged stale; the claim is deleted afterwards. The id names one file instance: its record's
// random token or, for a record that never became readable, the file's device, inode, modification time and size. A
// claim whose own claimant is stale is removed the same way, up to claimDepthLimit levels of claims.
// Invariant: an instance is removed only by its unique claimant, and ids are never reused, so a lock or claim taken
// since by a live run carries another id and survives; no live lock can be removed.
// Returns true when the caller should look again at once (the instance, or the claim in its way, was removed or found
// gone), and false while a live claimant is at work.
async function removeStaleInstance(file, stale, depth) {
  const claim = `${file}.claim-${stale.record?.token ?? stale.identity}`;
  const claimant = newLockRecord();
  if (!(await createExclusive(claim, claimant))) {
    const other = await readLockRecord(claim);
    if (other === null) {
      return true;
    }
    if (depth + 1 < claimDepthLimit && isStale(other)) {
      return removeStaleInstance(claim, other, depth + 1);
    }
    return false;
  }
  let failed = true;
  try {
    if (await removeIfUnchanged(file, stale)) {
      const subject = depth === 0 ? 'OpenAPI generation lock' : 'OpenAPI generation lock claim';
      process.stderr.write(`removed the stale ${subject} ${toRepoRelative(file)} left by ${describeHolder(stale)}\n`);
    }
    failed = false;
    return true;
  } finally {
    await cleanUpAfter(failed, toRepoRelative(claim), () => removeIfUnchanged(claim, { record: claimant }));
  }
}

// Removes the claim and record-update files that earlier runs left beside the lock. Once this run holds the lock,
// every claim refers to a lock instance already gone (nobody claims a live lock), and every other writer of an update
// has lost the lock, so removing them, even a claim a slow waiter still holds, cannot cost any live lock. A file that
// cannot be removed is reported and left for the next run.
async function removeLockLeftovers() {
  const lockDir = path.dirname(generationLock);
  const lockName = path.basename(generationLock);
  let names;
  try {
    names = await fs.readdir(lockDir);
  } catch (error) {
    process.stderr.write(`could not list the files beside ${toRepoRelative(generationLock)}: ${errorMessage(error)}\n`);
    return;
  }
  for (const name of names) {
    if (name.startsWith(`${lockName}.claim-`) || (name.startsWith(`${lockName}.`) && name.endsWith('.next'))) {
      const leftover = path.join(lockDir, name);
      try {
        await fs.rm(leftover, { force: true });
      } catch (error) {
        process.stderr.write(`could not remove ${toRepoRelative(leftover)}: ${errorMessage(error)}\n`);
      }
    }
  }
}

async function releaseGenerationLock(lock) {
  try {
    await removeIfUnchanged(generationLock, { record: { token: lock.token } });
    process.removeListener('exit', lock.releaseOnExit);
  } catch (error) {
    throw new GenerationError(
      `could not release the OpenAPI generation lock ${toRepoRelative(generationLock)} (${errorMessage(error)}); ` +
        'if no api:generate or lint run of this checkout is active, delete that file',
    );
  } finally {
    lock.leaveQueue();
  }
}

// Runs from the process 'exit' event, so it is synchronous and never throws: a lock it cannot remove names a
// process that no longer runs, and the next run removes it as stale. A lock whose recorded child still runs is kept,
// because that child goes on writing the checkout's outputs; it turns stale when the child ends.
function releaseLockFileSync(token) {
  try {
    const record = parseLockRecord(readFileSync(generationLock, 'utf8'));
    if (record?.token === token && !isChildRunning(record)) {
      unlinkSync(generationLock);
    }
  } catch {
    // Nothing more can be done while the process exits.
  }
}

// Rewrites the held lock's record with childPid set to the given pid, or without childPid when it is undefined. The
// new record is written whole to `<lock>.<token>.next` and renamed over the lock, so a reader sees the old record or
// the new one and never a partial one; the lock must still carry this run's token, or the update fails.
async function recordLockChild(lock, childPid) {
  const { childPid: previous, ...record } = lock.record;
  if (childPid !== undefined) {
    record.childPid = childPid;
  }
  const update = `${generationLock}.${lock.token}.next`;
  try {
    await fs.writeFile(update, `${JSON.stringify(record)}\n`);
    const current = await readLockRecord(generationLock);
    if (current?.record?.token !== lock.token) {
      throw new Error(
        current === null ? 'the lock file no longer exists' : `the lock is now held by ${describeHolder(current)}`,
      );
    }
    await renameReplacing(update, generationLock);
  } catch (error) {
    await cleanUpAfter(true, toRepoRelative(update), () => fs.rm(update, { force: true }));
    const change =
      childPid === undefined ? `clear child process ${previous} from` : `record child process ${childPid} in`;
    throw new GenerationError(
      `could not ${change} the OpenAPI generation lock ${toRepoRelative(generationLock)} (${errorMessage(error)})`,
    );
  }
  lock.record = record;
}

// Renames a file over an existing one. Windows refuses the replacement while another process has the target open,
// which waiting runs do for an instant on each poll, so those refusals are retried a few times.
async function renameReplacing(source, target) {
  for (let attempt = 1; ; attempt += 1) {
    try {
      await fs.rename(source, target);
      return;
    } catch (error) {
      if (attempt >= 5 || !['EPERM', 'EACCES', 'EBUSY'].includes(error.code)) {
        throw error;
      }
    }
    await sleep(50);
  }
}

function newLockRecord() {
  return { pid: process.pid, hostname: os.hostname(), token: randomUUID(), acquiredAt: new Date().toISOString() };
}

// Creates the file only if it does not exist and writes the record into it. Returns false when the file exists. A
// record that cannot be written completely is removed again, so it never stays behind as a lock.
async function createExclusive(file, record) {
  let handle;
  try {
    handle = await fs.open(file, 'wx');
  } catch (error) {
    if (error.code === 'EEXIST') {
      return false;
    }
    throw error;
  }
  try {
    await handle.writeFile(`${JSON.stringify(record)}\n`);
    await handle.close();
  } catch (error) {
    await cleanUpAfter(true, toRepoRelative(file), async () => {
      await handle.close();
      await fs.rm(file, { force: true });
    });
    throw error;
  }
  return true;
}

// Reads a lock or claim file as { record, identity, mtimeMs }, where record is null while the file is empty or holds
// no valid record, or returns null when the file does not exist. identity names this file instance in a form that
// can be part of a file name.
async function readLockRecord(file) {
  let handle;
  try {
    handle = await fs.open(file, 'r');
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
  try {
    const stats = await handle.stat();
    const record = parseLockRecord(await handle.readFile('utf8'));
    return { record, identity: `${stats.dev}-${stats.ino}-${stats.mtimeMs}-${stats.size}`, mtimeMs: stats.mtimeMs };
  } finally {
    await handle.close();
  }
}

function parseLockRecord(text) {
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  // The token becomes part of a claim file name, so only letters, digits and '-', as in a UUID, are accepted.
  const valid =
    typeof value === 'object' &&
    value !== null &&
    isPid(value.pid) &&
    (value.childPid === undefined || isPid(value.childPid)) &&
    typeof value.hostname === 'string' &&
    typeof value.token === 'string' &&
    /^[0-9A-Za-z-]{1,64}$/.test(value.token);
  return valid ? value : null;
}

function isPid(value) {
  return Number.isInteger(value) && value > 0;
}

function isStale(holder) {
  const { record } = holder;
  if (record === null) {
    return Date.now() - holder.mtimeMs > lockRecordGraceMilliseconds;
  }
  if (record.hostname !== os.hostname()) {
    return false;
  }
  const holderEnded = record.pid === process.pid || !isProcessRunning(record.pid);
  return holderEnded && !isChildRunning(record);
}

// This process is never a child that a record names, so a childPid equal to its pid is one that has ended.
function isChildRunning(record) {
  return record.childPid !== undefined && record.childPid !== process.pid && isProcessRunning(record.childPid);
}

// Signal 0 only checks for the process: ESRCH means it is gone, EPERM that it runs under another account. A process
// that has ended but was not yet reaped still answers it; a dotnet child whose parent was killed is reaped by the
// init process, which some containers never do. On Linux /proc reports such a process as Z (zombie) or X (dead).
function isProcessRunning(pid) {
  try {
    process.kill(pid, 0);
  } catch (error) {
    return error.code !== 'ESRCH';
  }
  if (process.platform !== 'linux') {
    return true;
  }
  let stat;
  try {
    stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
  } catch (error) {
    // Gone since the signal; any other failure leaves the signal's answer standing.
    return error.code !== 'ENOENT';
  }
  // The state follows the command name, which is in parentheses and may itself contain ') '.
  const state = stat.slice(stat.lastIndexOf(')') + 2, stat.lastIndexOf(')') + 3);
  return state !== 'Z' && state !== 'X';
}

// Removes the file while it still holds the expected holder: the same token, or, for a record that was never
// readable, the same file unchanged. Returns whether it removed the file.
async function removeIfUnchanged(file, expected) {
  const current = await readLockRecord(file);
  if (current === null || !isSameHolder(current, expected)) {
    return false;
  }
  try {
    await fs.unlink(file);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return false;
    }
    throw error;
  }
  return true;
}

function isSameHolder(current, expected) {
  if (current.record !== null || expected.record !== null) {
    return current.record?.token === expected.record?.token;
  }
  return current.identity === expected.identity;
}

function describeHolder(holder) {
  const record = holder?.record;
  if (!record) {
    return 'a run whose record is not readable';
  }
  const child = record.childPid === undefined ? '' : ` with its child process ${record.childPid}`;
  return `pid ${record.pid} on ${record.hostname}${child}`;
}

// Publication. The document and the client are one contract pair, so a failed run must never leave a new document
// beside an old client, or the reverse. Both fresh files, and a copy of each committed file, are first staged beside
// their targets under names carrying a new random token, and nothing committed is touched until all of them exist;
// a target that is not a regular file, such as a symbolic link, is refused while staging. Each fresh file is then
// renamed over its target, which replaces the target whole. When the second rename fails, the first target is put
// back as it was (its previous bytes, or no file) before the error is raised. The staged files are removed on every
// path, so nothing is left beside the committed pair, with one exception: when putting the first target back fails,
// its staged copy of the previous bytes is the only one left, so it is kept and the error names it.
async function publishFresh(fresh) {
  const token = randomUUID();
  const pair = [
    { source: fresh.json, target: COMMITTED.json },
    { source: fresh.ts, target: COMMITTED.ts },
  ].map((entry) => ({
    ...entry,
    staged: `${entry.target}.${token}.tmp`,
    original: `${entry.target}.${token}.orig.tmp`,
    existed: false,
  }));
  // Every name a copy is started for, so that cleanup touches only the files this run may have created.
  const stagedFiles = [];
  let failed = true;
  try {
    try {
      for (const entry of pair) {
        await fs.mkdir(path.dirname(entry.target), { recursive: true });
      }
      for (const entry of pair) {
        stagedFiles.push(entry.staged);
        // Byte for byte: check-api-fresh.mjs compares these files with the generator's output.
        await fs.copyFile(entry.source, entry.staged, fs.constants.COPYFILE_EXCL);
        entry.existed = await stageOriginal(entry, stagedFiles);
      }
    } catch (error) {
      throw new GenerationError(
        `publication failed while staging the generated files (${errorMessage(error)}); no committed file was changed`,
      );
    }

    const [first, second] = pair;
    try {
      await fs.rename(first.staged, first.target);
    } catch (error) {
      throw new GenerationError(
        `publication failed: could not replace ${toRepoRelative(first.target)} (${errorMessage(error)}); ` +
          'no committed file was changed',
      );
    }
    try {
      await fs.rename(second.staged, second.target);
    } catch (error) {
      throw await restoreFirstTarget(first, second, error, stagedFiles);
    }

    for (const entry of pair) {
      process.stdout.write(`wrote ${toRepoRelative(entry.target)}\n`);
    }
    failed = false;
  } finally {
    await cleanUpAfter(failed, 'the staged publication files', () => removeStaged(stagedFiles));
  }
}

// Copies the committed file beside itself so that a failed publication can put it back, and reports whether it
// existed. The target is examined without following links, because a rename replaces a symbolic link itself and a
// restore could not put it back: anything other than a regular file, a dangling link included, stops publication
// before either target is touched. The target is examined again after the copy, so a file replaced meanwhile, which
// the copy may have followed to other bytes, stops publication too.
async function stageOriginal(entry, stagedFiles) {
  const target = toRepoRelative(entry.target);
  let before;
  try {
    before = await fs.lstat(entry.target);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return false;
    }
    throw error;
  }
  if (!before.isFile()) {
    const kind = before.isSymbolicLink()
      ? 'a symbolic link'
      : before.isDirectory()
        ? 'a directory'
        : 'a special file';
    throw new Error(`${target} is ${kind}; only a regular file is replaced, so make it one and run again`);
  }
  stagedFiles.push(entry.original);
  await fs.copyFile(entry.target, entry.original, fs.constants.COPYFILE_EXCL);
  const after = await fs.lstat(entry.target);
  if (!after.isFile() || after.dev !== before.dev || after.ino !== before.ino) {
    throw new Error(`${target} was replaced while it was being copied; run again`);
  }
  return true;
}

// Called after the first target was replaced and the second was not: puts the first back from its staged original,
// or removes it when it did not exist before, and returns the error that states what the committed pair now holds.
// When the first cannot be put back, its staged original is the only copy of its previous bytes, so it is taken out
// of stagedFiles, which the cleanup removes, and the error names it.
async function restoreFirstTarget(first, second, cause, stagedFiles) {
  const replaced = toRepoRelative(first.target);
  const unchanged = toRepoRelative(second.target);
  const reason = `publication failed: could not replace ${unchanged} (${errorMessage(cause)})`;
  const inconsistent =
    `the new ${replaced} remains beside the unchanged ${unchanged}, so the committed pair may be inconsistent`;
  if (!first.existed) {
    try {
      await fs.rm(first.target, { force: true });
    } catch (removeError) {
      return new GenerationError(
        `${reason}, and removing ${replaced}, which did not exist before, also failed ` +
          `(${errorMessage(removeError)}); ${inconsistent}: delete ${replaced}, or run npm run api:generate again`,
      );
    }
    return new GenerationError(
      `${reason}; ${replaced} was removed again, as it did not exist before, so neither committed file changed`,
    );
  }
  try {
    await fs.rename(first.original, first.target);
  } catch (restoreError) {
    const index = stagedFiles.indexOf(first.original);
    if (index !== -1) {
      stagedFiles.splice(index, 1);
    }
    const recovery = toRepoRelative(first.original);
    const failure = `${reason}, and restoring ${replaced} also failed (${errorMessage(restoreError)}); ${inconsistent}`;
    // A failed rename leaves its source in place, so the copy is named as kept unless it is known to be gone.
    let kept = true;
    try {
      await fs.lstat(first.original);
    } catch (error) {
      kept = error.code !== 'ENOENT';
    }
    if (!kept) {
      return new GenerationError(
        `${failure}; its staged copy ${recovery} is gone too, so run npm run api:generate again`,
      );
    }
    return new GenerationError(
      `${failure}; its previous content is kept in ${recovery}: move that file back over ${replaced}, or run ` +
        'npm run api:generate again',
    );
  }
  return new GenerationError(
    `${reason}; ${replaced} was restored to its previous content, so neither committed file changed`,
  );
}

// Removes every staged name that still exists; a renamed one is already gone. Each removal is attempted, and a
// failure names every file left behind.
async function removeStaged(stagedFiles) {
  const leftovers = [];
  for (const file of stagedFiles) {
    try {
      await fs.rm(file, { force: true });
    } catch (error) {
      leftovers.push(`${toRepoRelative(file)} (${errorMessage(error)})`);
    }
  }
  if (leftovers.length > 0) {
    throw new GenerationError(`could not remove ${leftovers.join(', ')}; delete what is left by hand`);
  }
}

// Runs a cleanup step once the operation it follows has ended, without letting it hide that operation's outcome.
// After a failed operation a cleanup failure is only reported, on one stderr line, and the caller's own error goes on
// unchanged, so its message and exit status survive; after a successful operation the cleanup failure is the error.
async function cleanUpAfter(operationFailed, subject, cleanup) {
  try {
    await cleanup();
  } catch (error) {
    if (!operationFailed) {
      throw error;
    }
    process.stderr.write(`could not clean up ${subject}: ${errorMessage(error)}\n`);
  }
}

function errorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

// Runs a command without a shell, with inherited stdin, stderr and environment, and stdout inherited too or, with
// captureStdout, collected and returned as text. A failed start or a failed exit becomes a GenerationError that keeps
// the command's exit status, after any captured output has been passed on to stderr. Given the held lock, the child's
// pid is recorded in the lock for as long as the child runs, so the lock outlives this process while a child goes on
// writing the checkout's outputs; a child whose pid cannot be recorded is stopped before the error is raised.
async function runChecked(command, args, cwd, description, startFailureMessage, { lock, captureStdout = false } = {}) {
  let child;
  try {
    child = spawn(command, args, { cwd, stdio: ['inherit', captureStdout ? 'pipe' : 'inherit', 'inherit'] });
  } catch (error) {
    throw new GenerationError(startFailureMessage(error));
  }
  const startError = new Promise((resolve) => child.on('error', resolve));
  const exit = new Promise((resolve) => child.once('close', (status, signal) => resolve({ status, signal })));
  const chunks = [];
  child.stdout?.on('data', (chunk) => chunks.push(chunk));
  // Node leaves pid undefined exactly when the process could not be started, and then emits 'error'.
  if (child.pid === undefined) {
    throw new GenerationError(startFailureMessage(await startError));
  }
  if (lock !== undefined) {
    try {
      await recordLockChild(lock, child.pid);
    } catch (error) {
      child.kill();
      await exit;
      throw error;
    }
  }
  const { status, signal } = await exit;
  if (lock !== undefined) {
    await cleanUpAfter(status !== 0, toRepoRelative(generationLock), () => recordLockChild(lock, undefined));
  }
  const output = Buffer.concat(chunks).toString('utf8');
  if (status !== 0) {
    process.stderr.write(output);
    const reason = status === null ? `was terminated by signal ${signal}` : `exited with code ${status}`;
    throw new GenerationError(`${description} ${reason}`, status ?? 1);
  }
  return output;
}

function dotnetStartFailure(error) {
  return (
    `the dotnet command could not be started (${error.code ?? error.message}); ` +
    'the .NET SDK 10.0.1xx is required, see the root README.md'
  );
}

// The characters each step cannot take in a path, besides control characters, which can end a command or vanish
// from a URL and are refused everywhere. The OpenAPI target of Microsoft.Extensions.ApiDescription.Server runs its
// generator through MSBuild's shell-backed Exec, with the NuGet package folder, the checkout's build outputs and the
// document directory inside double quotes, where these still expand or end the quoting (sh: " $ ` \, cmd: " % !).
// Spaces and other punctuation stay literal there.
const passedToShell = "which the OpenAPI generator's build step passes to a shell";
const shellInDoubleQuotes = Object.freeze({
  characters: process.platform === 'win32' ? '"%!' : '"$`\\',
  effect: passedToShell,
});
// On POSIX, MSBuild's Exec runs `sh -c ". <script>"` with its script path unquoted, or quoted so that the argument
// splits, so the directory it writes scripts to must also avoid what sh splits or reads as syntax there, braces that
// bash expands, and '%', which makes dotnet itself crash. On Windows that path is quoted for cmd like the others.
const shellUnquoted = Object.freeze({
  characters: `${shellInDoubleQuotes.characters}${process.platform === 'win32' ? '' : " '&;|<>(){}%"}`,
  effect: passedToShell,
});
// openapi-typescript runs in the document directory and reads its working directory as URL text, where '%' starts an
// escape, '?' a query and '#' a fragment, so it would read another path or fail.
const urlText = Object.freeze({ characters: '%?#', effect: 'which openapi-typescript reads as URL syntax' });

function assertPathCharacters(file, subject, rule, remedy) {
  const offending = offendingCharacters(file, rule.characters);
  if (offending.size > 0) {
    const listed = [...offending].map((character) => JSON.stringify(character)).join(', ');
    throw new GenerationError(`the ${subject} ${JSON.stringify(file)} contains ${listed}, ${rule.effect}; ${remedy}`);
  }
}

// The control characters, and the characters of the given set, that the text contains.
function offendingCharacters(text, characters) {
  const offending = new Set();
  for (const character of text) {
    const code = character.codePointAt(0);
    if (code <= 0x1f || code === 0x7f || characters.includes(character)) {
      offending.add(character);
    }
  }
  return offending;
}

// The evaluated MSBuild properties from which the OpenAPI target of Microsoft.Extensions.ApiDescription.Server builds
// its generator command: these inside double quotes, NuGetPackageRoot as the start of the targets file's own folder
// (the restore's package imports load the targets from it, or from the NuGetPackageFolders entry that holds the
// package), and OpenApiGenerateDocumentsOptions appended unquoted. MSBuild reads every environment variable as a
// property, and an imported file such as Directory.Build.props can set any of them, so their values are read back
// from MSBuild's own evaluation of the project rather than predicted.
const quotedGeneratorProperties = Object.freeze([
  'NuGetPackageRoot',
  'TargetPath',
  '_OpenApiDocumentsCache',
  'TargetFrameworkMoniker',
  'OpenApiDocumentsDirectory',
  'MSBuildProjectName',
  'ProjectAssetsFile',
  'PlatformTarget',
  'Platform',
  'RuntimeIdentifier',
]);
const evaluatedPropertySource =
  'an environment variable or an imported file such as Directory.Build.props can set it, or a property it is built ' +
  'from';

// Evaluates the API project with the build's own global properties, after the restore that writes the package imports
// defining NuGetPackageRoot (an evaluation never restores), and refuses every value the generator command could not
// carry as plain text, and a cache marker other than the one pinned in marker, before the build runs that command.
async function assertGeneratorCommandInputs(project, buildProperties, cwd, dir, marker, lock) {
  const names = [
    ...quotedGeneratorProperties,
    'NuGetPackageFolders',
    'OpenApiGenerateDocumentsOptions',
    'MSBuildProjectDirectory',
  ];
  const description = `dotnet msbuild ${toRepoRelative(apiProject)} -getProperty`;
  const output = await runChecked(
    'dotnet',
    ['msbuild', project, ...buildProperties, '-nologo', ...names.map((name) => `-getProperty:${name}`)],
    cwd,
    description,
    dotnetStartFailure,
    { lock, captureStdout: true },
  );
  const properties = parseEvaluatedProperties(output, names);
  if (properties === null) {
    process.stderr.write(output);
    throw new GenerationError(`${description} printed no text value for each of ${names.join(', ')}`);
  }

  const options = properties.OpenApiGenerateDocumentsOptions;
  if (options !== '') {
    throw new GenerationError(
      `the evaluated MSBuild property OpenApiGenerateDocumentsOptions is ${JSON.stringify(options)}; the OpenAPI ` +
        "generator's build step appends it to a shell command unquoted, and any option in it would change the " +
        `committed document; ${evaluatedPropertySource}, so unset it`,
    );
  }
  if (properties.NuGetPackageRoot === '') {
    throw new GenerationError(
      'the evaluated MSBuild property NuGetPackageRoot is empty after dotnet restore, so the folder the OpenAPI ' +
        "generator's build step runs the generator from cannot be checked; an environment variable or an imported " +
        'file such as Directory.Build.props can redirect or disable the restore output it comes from; unset or ' +
        'change that setting',
    );
  }
  if (properties.OpenApiDocumentsDirectory !== dir) {
    throw new GenerationError(
      `${description} read the document directory ${JSON.stringify(dir)} as ` +
        `${JSON.stringify(properties.OpenApiDocumentsDirectory)}, so the generator would write elsewhere`,
    );
  }
  for (const name of quotedGeneratorProperties) {
    assertEvaluatedProperty(name, properties[name]);
  }
  for (const folder of properties.NuGetPackageFolders.split(';')) {
    if (folder !== '') {
      assertEvaluatedProperty('NuGetPackageFolders entry', folder);
    }
  }
  assertPinnedCacheMarker(properties, marker, description);
}

// Checks that the generator target's cache marker is the one generateFresh pins with -p:_OpenApiDocumentsCache. The
// target resolves the property, like any path, from the project directory, and on POSIX MSBuild reads a '\' in a path
// as '/', so the value is compared as the path MSBuild resolves, never as text. A global property wins over every
// assignment unless the project declares it local (TreatAsLocalProperty), in which case the build would skip on, and
// write, a marker outside the run's directory.
function assertPinnedCacheMarker(properties, marker, description) {
  const projectDir = properties.MSBuildProjectDirectory;
  const configured = properties._OpenApiDocumentsCache;
  if (!path.isAbsolute(projectDir)) {
    throw new GenerationError(
      `${description} read MSBuildProjectDirectory as ${JSON.stringify(projectDir)}, which is not absolute, so the ` +
        "OpenAPI generator's cache marker cannot be located",
    );
  }
  const resolved = path.resolve(
    projectDir,
    process.platform === 'win32' ? configured : configured.replaceAll('\\', '/'),
  );
  if (resolved !== marker) {
    throw new GenerationError(
      `the evaluated MSBuild property _OpenApiDocumentsCache ${JSON.stringify(configured)} names ` +
        `${JSON.stringify(resolved)}, not ${JSON.stringify(marker)}, the cache marker this run pins into its own ` +
        "directory so that the OpenAPI generator's build step always runs; the project or an imported file declares " +
        'it a local property (TreatAsLocalProperty), so remove that declaration',
    );
  }
}

// Removes src/EpiInfo.Api/obj/EpiInfo.Api.OpenApiFiles.cache, the marker a build without the pin in generateFresh
// writes in the default layout. Generation no longer depends on it, so it is deleted only when it is certainly this
// checkout's own leftover: a regular file in a directory that, symbolic links resolved, lies inside the checkout (the
// canonical path generateFresh passes in). A missing directory or file, a directory that resolves elsewhere, such as a
// symlinked or shared obj/, and anything other than a regular file are left untouched, and the run goes on.
async function removeObjLeftoverMarker(checkout) {
  const objDir = path.join(checkout, path.relative(repoRoot, path.dirname(defaultOpenApiCacheMarker)));
  let canonicalObjDir;
  try {
    canonicalObjDir = await fs.realpath(objDir);
  } catch (error) {
    if (error.code === 'ENOENT' || error.code === 'ENOTDIR') {
      return;
    }
    throw new GenerationError(`could not resolve ${JSON.stringify(objDir)} (${errorMessage(error)})`);
  }
  const fromCheckout = path.relative(checkout, canonicalObjDir);
  if (fromCheckout === '..' || fromCheckout.startsWith(`..${path.sep}`) || path.isAbsolute(fromCheckout)) {
    return;
  }
  // The file is examined and removed through the resolved directory the check above accepted.
  const leftover = path.join(canonicalObjDir, openApiCacheMarkerName);
  let stats;
  try {
    stats = await fs.lstat(leftover);
  } catch (error) {
    if (error.code === 'ENOENT' || error.code === 'ENOTDIR') {
      return;
    }
    throw new GenerationError(`could not examine ${JSON.stringify(leftover)} (${errorMessage(error)})`);
  }
  if (!stats.isFile()) {
    return;
  }
  try {
    await fs.unlink(leftover);
  } catch (error) {
    if (error.code !== 'ENOENT') {
      throw new GenerationError(`could not delete ${JSON.stringify(leftover)} (${errorMessage(error)})`);
    }
  }
}

// With several -getProperty switches MSBuild prints {"Properties": {...}} on stdout. The dotnet CLI may print notices
// before it, so the object is taken from the first line that opens one to the last line that closes one.
function parseEvaluatedProperties(output, names) {
  const json = /^\{[\s\S]*^\}/m.exec(output)?.[0];
  if (json === undefined) {
    return null;
  }
  let properties;
  try {
    properties = JSON.parse(json)?.Properties;
  } catch {
    return null;
  }
  const complete =
    typeof properties === 'object' && properties !== null && names.every((name) => typeof properties[name] === 'string');
  return complete ? properties : null;
}

// Checks an evaluated value that the generator command holds inside double quotes. On POSIX, MSBuild turns '\' into
// '/' only in text whose first segment names an existing folder, so any other '\' reaches sh as written; inside double
// quotes sh keeps it literal except before another '\', which it collapses, or before the closing quote, which it
// escapes, so the quoting would end elsewhere. Such a '\' is refused, as are the other characters of
// shellInDoubleQuotes; a lone '\' within the value, as in the default obj\EpiInfo.Api.OpenApiFiles.cache, is kept.
function assertEvaluatedProperty(subject, value) {
  const posix = process.platform !== 'win32';
  const characters = posix ? shellInDoubleQuotes.characters.replace('\\', '') : shellInDoubleQuotes.characters;
  const listed = [...offendingCharacters(value, characters)].map((character) => JSON.stringify(character));
  if (posix && /\\(?:\\|$)/.test(value)) {
    listed.push(`${JSON.stringify('\\')} at its end or before another ${JSON.stringify('\\')}`);
  }
  if (listed.length > 0) {
    throw new GenerationError(
      `the evaluated MSBuild property ${subject} ${JSON.stringify(value)} contains ${listed.join(', ')}, ` +
        `${shellInDoubleQuotes.effect}; ${evaluatedPropertySource}, so unset or change that setting`,
    );
  }
}

// MSBuild splits a -p: value on ',' and ';', decodes %XX in it, and reads $ @ ' ( ) * ? as property, item, quoting
// and wildcard syntax where the target expands it. Escaping each as %XX hands the target the path verbatim.
function escapeMsbuildPropertyValue(value) {
  return value.replace(
    /[%;,$@'()*?]/g,
    (character) => `%${character.charCodeAt(0).toString(16).toUpperCase().padStart(2, '0')}`,
  );
}

// The CLI entry point of the locally installed openapi-typescript, read from its package.json by path.
async function openapiTypescriptCli() {
  let manifest;
  try {
    manifest = JSON.parse(await fs.readFile(openapiTsPackageJson, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') {
      throw new GenerationError('openapi-typescript is not installed; run npm ci in web/');
    }
    throw error;
  }
  const bin = typeof manifest?.bin === 'string' ? manifest.bin : manifest?.bin?.['openapi-typescript'];
  if (typeof bin !== 'string' || bin.length === 0) {
    throw new GenerationError(`${toRepoRelative(openapiTsPackageJson)} declares no openapi-typescript executable`);
  }
  return path.join(path.dirname(openapiTsPackageJson), bin);
}

async function isFile(file) {
  try {
    return (await fs.stat(file)).isFile();
  } catch (error) {
    if (error.code === 'ENOENT') {
      return false;
    }
    throw error;
  }
}

function toRepoRelative(file) {
  return path.relative(repoRoot, file).split(path.sep).join('/');
}

async function writeCommitted() {
  const fresh = await generateFresh();
  let published = false;
  try {
    await publishFresh(fresh);
    published = true;
  } finally {
    await cleanUpAfter(!published, fresh.dir, () => removeFresh(fresh.dir));
  }
}

// Importing this module, as check-api-fresh.mjs does, must not rewrite the committed files. Node names the main
// module by its real path, while process.argv[1] keeps the path as typed, so a run through a symlinked file or
// directory is recognised only by comparing canonical paths. No argv[1] (node -e), or a path on either side that
// cannot be resolved (node - reading stdin), means this module is not the script being run.
const isMain =
  process.argv[1] !== undefined &&
  (await Promise.all([
    fs.realpath(fileURLToPath(import.meta.url)),
    fs.realpath(path.resolve(process.argv[1])),
  ]).then(
    ([self, entry]) => self === entry,
    () => false,
  ));

if (isMain) {
  try {
    await writeCommitted();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`api:generate failed: ${message}\n`);
    process.exitCode = error instanceof GenerationError ? error.exitCode : 1;
  }
}
