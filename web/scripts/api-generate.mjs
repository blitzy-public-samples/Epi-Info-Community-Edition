// Regenerates the committed OpenAPI document (src/EpiInfo.Api/openapi/EpiInfo.Api.json) and the client types
// generated from it (web/src/api/schema.ts).
//
// Usage, from web/ after any change to the API contract:  npm run api:generate
// `node scripts/api-generate.mjs --help` (or -h) prints the usage and changes nothing; the script takes no other
// argument, and any other argument is refused with exit status 2 before anything runs (see handleArguments).
//
// Requires the .NET SDK 10.0.1xx (see the root README.md) and `npm ci` already run in web/. No database is needed:
// the build-time generator hosts Program without starting Kestrel. Nothing is added to the environment the build
// and the CLI inherit, so no connection string is read, set or printed here.
//
// check-api-fresh.mjs, the second half of `npm run lint`, reuses generateFresh() and compares its output with
// COMMITTED, so both scripts produce the documents in exactly the same way.

import { spawn, spawnSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import {
  closeSync,
  fstatSync,
  lstatSync,
  mkdtempSync,
  openSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { clearInterval, setInterval, setTimeout } from 'node:timers';
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

/**
 * A generation failure; exitCode carries the status of the failing dotnet or openapi-typescript run, or 128 + n for a
 * run interrupted by signal n (see currentInterruption).
 */
export class GenerationError extends Error {
  constructor(message, exitCode = 1) {
    super(message);
    this.name = 'GenerationError';
    this.exitCode = Number.isInteger(exitCode) && exitCode !== 0 ? exitCode : 1;
  }
}

/**
 * A refusal by assertInsideCheckout: the file lies outside the checkout, or one of the directories it is reached
 * through is a symbolic link, a junction or something else that is not a directory of this checkout.
 */
export class CheckoutPathError extends GenerationError {
  constructor(message) {
    super(message);
    this.name = 'CheckoutPathError';
  }
}

/**
 * Checks that every directory between the repository checkout and the file is a directory of this checkout, so that
 * reading, creating, replacing or removing the file never leaves the checkout. The directories from the checkout down
 * to the file's own are examined top-down with lstat, which follows no link: a symbolic link (a Windows junction
 * included, which lstat reports as one) or anything else that is not a directory is refused with a CheckoutPathError
 * naming it, and so is a file that lies outside the checkout. The checkout's own location, and any link that leads to
 * it, are not examined. With createMissing, a missing directory is created on its own, never recursively, and then
 * examined like the others, so nothing is ever created through a link; without it, the lstat failure (ENOENT) is
 * thrown unchanged. The file itself is not examined: each caller opens, creates or replaces it in a way that never
 * follows a link in its place. The directories are examined as they are when the check runs, which is why publication
 * and every operation on the generation lock check them again immediately before they act.
 */
export async function assertInsideCheckout(file, { createMissing = false } = {}) {
  for (const directory of checkoutDirectories(file)) {
    let stats;
    try {
      stats = await fs.lstat(directory);
    } catch (error) {
      if (error.code !== 'ENOENT' || !createMissing) {
        throw error;
      }
      try {
        await fs.mkdir(directory);
      } catch (mkdirError) {
        // Created meanwhile by another run; it is examined like any other.
        if (mkdirError.code !== 'EEXIST') {
          throw mkdirError;
        }
      }
      stats = await fs.lstat(directory);
    }
    assertCheckoutDirectory(file, directory, stats);
  }
}

// assertInsideCheckout without createMissing, in one synchronous step, for the lock operations that must not yield to
// a signal handler (see recordLockChild and releaseLockFileSync). The same directories are examined, with lstatSync,
// and refused with the same CheckoutPathError messages.
function assertInsideCheckoutSync(file) {
  for (const directory of checkoutDirectories(file)) {
    assertCheckoutDirectory(file, directory, lstatSync(directory));
  }
}

// The directories between the checkout and the file, top-down and ending with the file's own, which both checks
// examine. A file that lies outside the checkout is refused.
function checkoutDirectories(file) {
  const relative = path.relative(repoRoot, path.resolve(file));
  if (relative === '' || relative === '..' || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) {
    throw new CheckoutPathError(
      `${JSON.stringify(file)} lies outside the checkout ${JSON.stringify(repoRoot)}; only files whose directories ` +
        'all belong to this checkout are read or written',
    );
  }
  const names = path.dirname(relative).split(path.sep);
  const directories = [];
  let directory = repoRoot;
  // A file directly in the checkout has the directory name '.', which names the checkout itself.
  for (const name of names[0] === '.' ? [] : names) {
    directory = path.join(directory, name);
    directories.push(directory);
  }
  return directories;
}

// Refuses a directory whose lstat reports a symbolic link (a Windows junction included) or anything else that is not
// a directory.
function assertCheckoutDirectory(file, directory, stats) {
  if (stats.isSymbolicLink() || !stats.isDirectory()) {
    const through = toRepoRelative(directory);
    const kind = stats.isSymbolicLink() ? 'a symbolic link or junction' : 'not a directory';
    throw new CheckoutPathError(
      `${toRepoRelative(file)} is reached through ${through}, which is ${kind}; only files whose directories all ` +
        `belong to this checkout are read or written, so make ${through} a directory of this checkout`,
    );
  }
}

/**
 * Builds the API with document generation forced on, then runs openapi-typescript on the result, all inside a new
 * temporary directory. Generation is forced by pinning the OpenAPI target's incremental marker into that directory, so
 * the target finds its output missing and runs in every build layout; the only marker deleted is a regular-file
 * leftover in the checkout's own src/EpiInfo.Api/obj/. Returns { json, ts, dir }: the fresh document, the fresh client
 * types and the directory that holds them, which the caller removes with removeFresh(dir), marker included. Until then
 * the run holds the checkout's generation lock, which every other run of this checkout waits for. A failed run removes
 * the directory and releases the lock itself. From the directory's creation until its removal, an interruption by
 * SIGINT, SIGTERM or SIGHUP stops every process the run started, with their descendants, fails the run through that
 * same cleanup, and then ends the process by the signal (see onInterruptionSignal).
 */
export async function generateFresh() {
  throwIfInterrupted();
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

  // The run opens, and its directory is created and recorded in it, in one synchronous step. A signal is handled only
  // between synchronous steps, so an interruption finds this run either not yet open, with nothing created, or open
  // with its directory recorded, which it then removes. An asynchronous mkdtemp would leave the directory existing but
  // unrecorded until it completed, and a forced stop in that window would leave it behind.
  const run = openRun();
  let dir;
  try {
    dir = mkdtempSync(path.join(tempRoot, 'epiinfo-openapi-'));
  } catch (error) {
    closeRun(run);
    throw error;
  }
  run.dir = dir;
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

/**
 * Removes a directory returned by generateFresh(), and releases the generation lock its run holds. The run stays open
 * until its directory is removed, so a directory that could not be removed is tried again if an interruption follows.
 * While an interruption is stopping the processes the run started, which may still write into the directory, nothing
 * is removed until all of them have ended with their descendants; the interruption bounds that wait, as it ends the
 * process once its own bounded waits are over (see finishInterruption).
 */
export async function removeFresh(dir) {
  if (interruption !== null) {
    await waitUntil(startedProcessesEnded, Infinity);
  }
  const lock = heldLocks.get(dir);
  if (lock === undefined) {
    await fs.rm(dir, { recursive: true, force: true });
    closeRunOf(dir);
    return;
  }
  heldLocks.delete(dir);
  // Both steps are always attempted, so a directory that cannot be removed never keeps the checkout locked.
  const [removal, release] = await Promise.allSettled([
    fs.rm(dir, { recursive: true, force: true }),
    releaseGenerationLock(lock),
  ]);
  if (removal.status === 'fulfilled') {
    closeRunOf(dir);
  }
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

// Interruption. Left to their default disposition, Ctrl-C (SIGINT), a termination request (SIGTERM) and the loss of
// the terminal (SIGHUP) end this process at once: the run's temporary directory stays behind, the generation lock
// stays until the next run removes it as stale, and a dotnet child goes on building and writes the document into the
// orphaned directory. So while at least one run is open, these signals are caught, and the first one:
//   - is remembered (see currentInterruption), and from then on nothing new starts: runChecked() spawns no process,
//     a run waiting for the lock stops waiting, and writeCommitted() starts no publication. A publication already
//     under way is never cut short: it completes, or rolls back as a whole, by its own logic;
//   - stops every process the run started, with its descendants. On POSIX, runChecked() starts each command detached,
//     as the leader of a new session and process group, which every descendant joins unless it changes group itself.
//     Having no controlling terminal, the tree never receives a terminal's Ctrl-C or hangup, which reach only this
//     process, and no background read or write of the terminal can stop it with SIGTTIN or SIGTTOU. Each group is
//     sent SIGINT, whichever signal arrived: SIGINT is MSBuild's graceful cancellation, while a dotnet sent SIGTERM
//     ends at once with status 0. A group still holding a live member after childStopGraceMilliseconds, such as a
//     dotnet build cancelled during its implicit restore, which can hang, or a descendant that ignores SIGINT, is
//     sent SIGKILL. A group counts as stopped only once none of its members is alive, however long ago its leader
//     ended (see groupHasLiveMember), and it stays tracked after its leader has ended for as long as members remain.
//     On Windows, which has neither signals nor process groups and where a detached child would open a console
//     window, each command still running has its process tree ended outright (see endProcessTree);
//   - lets the run fail through its own cleanup, generateFresh()'s failure path or the caller's removeFresh(dir),
//     which waits until every started process has ended with its descendants, then removes the directory and
//     releases the lock, and lets the caller report the interruption;
//   - then, synchronously (see finishInterruption), kills every process group still tracked, waits briefly for each
//     to empty, removes what is left, restores the default disposition and raises the signal again, so the process
//     still ends by it and its parent sees 128 + n.
// A second signal while stopping skips the waiting and goes straight to that last step. Either way, the run's
// directory is removed only once every process the run started, with its descendants, has ended or, in that last
// step, been killed. The directory and the lock are created and recorded synchronously, so no interruption finds one
// of them existing but unrecorded. What escapes: a descendant that leaves its process group (by setsid or setpgid) is
// not stopped, nor on Windows, where taskkill finds a tree by parent process, a descendant whose parent had already
// ended; and SIGKILL of this process, which cannot be caught, leaves the directory behind and the processes it started
// running to their end. Its lock, which records the running dotnet child, is removed as stale by the first run after
// that child has ended.
const interruptionSignals = Object.freeze(['SIGINT', 'SIGTERM', 'SIGHUP']);
const childStopGraceMilliseconds = 30 * 1000;
const childKillGraceMilliseconds = 5 * 1000;
const runSettleMilliseconds = 10 * 1000;
const interruptionPollMilliseconds = 50;
// How long the final, synchronous cleanup waits for the process groups it has just killed to empty.
const killedChildEndMilliseconds = 500;
// How long the process may outlive the signal it raises again before it exits with 128 + n instead.
const reRaiseGraceMilliseconds = 1000;
// How often a process group whose leader has ended is checked for having emptied (see onStartedProcessClosed).
const lingeringGroupPollMilliseconds = 1000;

// The runs still open, each as { dir }: from just before generateFresh() creates its temporary directory (dir is set
// once it exists) until that directory has been removed. The signals are caught exactly while one is open.
const openRuns = new Set();
// Each process runChecked() started, as { child, closed }, until it has ended with its descendants: on POSIX until its
// process group has no live member, which may be long after the child itself closed, and on Windows until it closes.
const startedProcesses = new Set();
// The timer that checks the groups whose leader has ended, while there are any.
let lingeringGroupWatch = null;
// How many publications of the committed pair are under way.
let publicationsUnderWay = 0;
// The first interruption, as { signal, exitCode }, or null while none has arrived.
let interruption = null;
let interruptionFinished = false;
// The standard streams whose terminal or pipe went away during an interruption.
const lostStreams = new Set();

/**
 * The interruption this process received while a run was open, as { signal, exitCode } with exitCode 128 + the
 * signal's number, or null. Once it is set, every run of this process fails, and the process ends by that signal as
 * soon as its runs have cleaned up, so callers report it in place of their usual outcome.
 */
export function currentInterruption() {
  return interruption;
}

function openRun() {
  if (openRuns.size === 0) {
    for (const signal of interruptionSignals) {
      process.on(signal, onInterruptionSignal);
    }
  }
  const run = { dir: undefined };
  openRuns.add(run);
  return run;
}

// Once no run is open, the default disposition is back, unless an interruption is ending the process, which then
// removes the listeners itself just before it raises the signal again.
function closeRun(run) {
  openRuns.delete(run);
  if (openRuns.size === 0 && interruption === null) {
    removeInterruptionListeners();
  }
}

function closeRunOf(dir) {
  for (const run of openRuns) {
    if (run.dir === dir) {
      closeRun(run);
    }
  }
}

function removeInterruptionListeners() {
  for (const signal of interruptionSignals) {
    process.removeListener(signal, onInterruptionSignal);
  }
}

function throwIfInterrupted() {
  if (interruption !== null) {
    throw new GenerationError(`the run was interrupted by ${interruption.signal}`, interruption.exitCode);
  }
}

function onInterruptionSignal(signal) {
  if (interruption !== null) {
    reportWhileStopping(`${signal} received again: stopping at once\n`);
    finishInterruption();
    return;
  }
  interruption = Object.freeze({ signal, exitCode: 128 + os.constants.signals[signal] });
  // A terminal or pipe that went away, the usual cause of SIGHUP, fails later writes with an asynchronous stream
  // error, which unhandled would end the process before its cleanup. A failed stream is recorded instead, and the
  // interruption writes nothing more to it.
  for (const stream of [process.stdout, process.stderr]) {
    stream.on('error', () => lostStreams.add(stream));
  }
  reportWhileStopping(`${signal} received: stopping the OpenAPI build and removing its temporary directory\n`);
  stopInterruptedRuns().then(finishInterruption, finishInterruption);
}

function reportWhileStopping(text) {
  if (!lostStreams.has(process.stderr)) {
    process.stderr.write(text);
  }
}

// The first interruption's orderly stop: the started processes first, with their descendants, then a publication
// under way, however long it takes, then the runs' own cleanup, each wait bounded except the publication's, which a
// second signal cuts short.
async function stopInterruptedRuns() {
  signalStartedProcesses('SIGINT');
  if (!(await waitUntil(startedProcessesEnded, childStopGraceMilliseconds))) {
    signalStartedProcesses('SIGKILL');
    await waitUntil(startedProcessesEnded, childKillGraceMilliseconds);
  }
  await waitUntil(() => publicationsUnderWay === 0, Infinity);
  await waitUntil(() => openRuns.size === 0, runSettleMilliseconds);
}

// Polls with a timer, which also keeps the event loop alive, until the condition holds or the time is up. Returns
// whether the condition holds.
async function waitUntil(condition, milliseconds) {
  const deadline = Date.now() + milliseconds;
  while (!condition()) {
    if (Date.now() >= deadline) {
      return false;
    }
    await sleep(interruptionPollMilliseconds);
  }
  return true;
}

// Sends the signal to every process runChecked() started that has not ended with its descendants.
function signalStartedProcesses(signal) {
  for (const started of startedProcesses) {
    signalStartedProcess(started, signal);
  }
}

// On POSIX the signal goes to the process's whole group. A group that emptied since it was last checked answers
// ESRCH; nothing more is needed for it. On Windows every signal ends the process tree outright.
function signalStartedProcess(started, signal) {
  if (process.platform === 'win32') {
    if (!started.closed) {
      endProcessTree(started.child);
    }
    return;
  }
  try {
    process.kill(-started.child.pid, signal);
  } catch {
    // The group has no member left to signal.
  }
}

// taskkill /T /F ends the process together with every process it started that still runs, which child.kill(), the
// fallback for a taskkill that cannot run, would leave running. Failures are ignored: a process that has ended already
// needs nothing more.
function endProcessTree(child) {
  spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
  child.kill();
}

// Whether the started process has ended with its descendants: on POSIX, whether its group has no live member left.
function hasEnded(started) {
  if (process.platform === 'win32') {
    return started.closed || !isProcessRunning(started.child.pid);
  }
  return !groupHasLiveMember(started.child.pid);
}

// Stops tracking every started process that has ended with its descendants, and returns whether none is left.
function startedProcessesEnded() {
  for (const started of startedProcesses) {
    if (hasEnded(started)) {
      startedProcesses.delete(started);
    }
  }
  return startedProcesses.size === 0;
}

// Runs when a started process closes. A group whose members outlive their leader stays tracked, and while any such
// group remains, a timer that does not keep the process alive checks it every lingeringGroupPollMilliseconds. Once a
// group has emptied, its id may be given to a new group, so it is dropped at the next check, long before the system's
// cycle of process ids could reuse that id, and is never signalled again.
function onStartedProcessClosed(started) {
  started.closed = true;
  if (hasEnded(started)) {
    startedProcesses.delete(started);
    return;
  }
  if (lingeringGroupWatch !== null) {
    return;
  }
  lingeringGroupWatch = setInterval(() => {
    startedProcessesEnded();
    if (![...startedProcesses].some((entry) => entry.closed)) {
      clearInterval(lingeringGroupWatch);
      lingeringGroupWatch = null;
    }
  }, lingeringGroupPollMilliseconds);
  lingeringGroupWatch.unref();
}

// Whether the process group still holds a live member. Signal 0 to the group fails with ESRCH once no member exists,
// and with EPERM when a member runs under another account, which counts as live. A member that has ended but was not
// yet reaped still answers it, and an orphaned descendant may never be reaped (see isProcessRunning), so on Linux the
// group's members are looked up in /proc, whose stat for each process names its group, and only members not in state
// Z (zombie) or X (dead) count. Elsewhere, and where /proc cannot be listed, the signal's answer stands.
function groupHasLiveMember(group) {
  try {
    process.kill(-group, 0);
  } catch (error) {
    return error.code !== 'ESRCH';
  }
  if (process.platform !== 'linux') {
    return true;
  }
  let pids;
  try {
    pids = readdirSync('/proc');
  } catch {
    return true;
  }
  for (const pid of pids) {
    if (!/^\d+$/.test(pid)) {
      continue;
    }
    let stat;
    try {
      stat = readFileSync(`/proc/${pid}/stat`, 'utf8');
    } catch {
      // Ended since the listing.
      continue;
    }
    const [state, , processGroup] = procStatFields(stat);
    if (Number(processGroup) === group && state !== 'Z' && state !== 'X') {
      return true;
    }
  }
  return false;
}

// Ends an interrupted process: kills every process group still tracked (on Windows, every process tree still running),
// removes every directory a run left open and releases every lock and claim file still held, all synchronously, then
// raises the signal again with the default disposition restored. A killed group takes a moment to empty, so that is
// awaited briefly first; a lock whose recorded child has still not ended is kept by releaseLockFileSync, and the next
// run removes it as stale. A directory that cannot be removed, a lock or claim that cannot be released and a lock kept
// for its child are each reported on one line.
function finishInterruption() {
  if (interruptionFinished) {
    return;
  }
  interruptionFinished = true;
  signalStartedProcesses('SIGKILL');
  const deadline = Date.now() + killedChildEndMilliseconds;
  const pause = new Int32Array(new SharedArrayBuffer(4));
  while (!startedProcessesEnded() && Date.now() < deadline) {
    Atomics.wait(pause, 0, 0, interruptionPollMilliseconds / 5);
  }
  for (const run of openRuns) {
    if (run.dir !== undefined) {
      try {
        rmSync(run.dir, { recursive: true, force: true });
      } catch (error) {
        reportWhileStopping(`could not remove ${run.dir}: ${errorMessage(error)}\n`);
      }
    }
  }
  for (const lock of heldLocks.values()) {
    releaseLockFileSync(lock.token, generationLock, reportWhileStopping);
  }
  for (const [claim, token] of heldClaims) {
    releaseLockFileSync(token, claim, reportWhileStopping);
  }
  removeInterruptionListeners();
  const { signal, exitCode } = interruption;
  process.exitCode = exitCode;
  // Where raising the signal does not end the process (Windows has no POSIX signals, and another listener for it
  // keeps the process alive), it exits with the status a shell reports for that signal.
  try {
    process.kill(process.pid, signal);
  } catch {
    process.exit(exitCode);
  }
  setTimeout(() => process.exit(exitCode), reRaiseGraceMilliseconds).unref();
}

// Generation lock. One file per checkout, under the API's git-ignored obj/, created exclusively by the run that holds
// it and recording { pid, hostname, token, acquiredAt }, plus childPid while a dotnet process the run started is
// writing the checkout's outputs: a child outlives a killed parent, so the lock must outlive it too. The lock is kept
// in this obj/ because it serializes the writers of exactly this obj/ and bin/, which a lock kept elsewhere could not
// do for builds that share a redirected obj/, so an obj/ that is, or is reached through, a symbolic link or junction
// is refused rather than followed. obj/ and every directory above it are checked to be directories of this checkout
// (see assertInsideCheckout) immediately before each operation on the lock, its claims and its record updates: each
// attempt to take the lock, the first of which creates a missing directory on its own, and each read, creation,
// removal and release. A refusal stops that operation, so none of those files is read, written or removed outside
// this checkout, and the first attempt refuses before anything is created or any build starts. Such a file is read
// without following a link in its place, which is refused, and is created only under a name that nothing holds yet. A
// waiting run polls, and gives up after the wait limit. A lock is stale when its record names this host, a holder that
// no longer runs or is this very process (which holds no lock while acquiring one), and no child that still runs; a
// record that never became readable is stale once older than the grace period. A stale lock is removed (see
// removeStaleInstance), never one held from another host. Within one process, generateFresh() calls queue first, so
// the file is contended only between processes and a process never waits for itself.
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
// The token of each claim file this process created while removing a stale lock, by the claim's path, until the claim
// has been removed again, so that an interruption that ends the process meanwhile removes it too.
const heldClaims = new Map();

async function acquireGenerationLock(dir) {
  const previous = generationQueue;
  let leaveQueue;
  generationQueue = new Promise((resolve) => {
    leaveQueue = resolve;
  });
  await previous;
  let lock;
  try {
    // acquireLockFile() creates the lock synchronously as its last step, and only promise continuations, never a
    // signal handler, run between that and the lines below, so the lock is recorded as held before an interruption
    // can look for it (see finishInterruption).
    const record = await acquireLockFile();
    // Safety net for a process that exits without removeFresh(dir); a release in removeFresh deregisters it.
    const releaseOnExit = () => releaseLockFileSync(record.token);
    process.on('exit', releaseOnExit);
    // record is the content of the lock file, which recordLockChild rewrites while a child runs.
    lock = { token: record.token, record, releaseOnExit, leaveQueue };
    heldLocks.set(dir, lock);
  } catch (error) {
    leaveQueue();
    throw error;
  }
  await removeLockLeftovers();
  return lock;
}

async function acquireLockFile() {
  const record = newLockRecord();
  const deadline = Date.now() + lockWaitLimitMilliseconds;
  let announced = false;
  for (let attempt = 1; ; attempt += 1) {
    // An interrupted run stops waiting at the next poll.
    throwIfInterrupted();
    await assertInsideCheckout(generationLock, { createMissing: attempt === 1 });
    if (createExclusive(generationLock, record)) {
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
  await assertInsideCheckout(claim);
  if (!createExclusive(claim, claimant)) {
    const other = await readLockRecord(claim);
    if (other === null) {
      return true;
    }
    if (depth + 1 < claimDepthLimit && isStale(other)) {
      return removeStaleInstance(claim, other, depth + 1);
    }
    return false;
  }
  // Recorded in the same synchronous step as its creation (see heldClaims).
  heldClaims.set(claim, claimant.token);
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
    heldClaims.delete(claim);
  }
}

// Removes the claim and record-update files that earlier runs left beside the lock, but only those whose recorded
// process has ended on this host, or whose record never became readable and is older than the grace period (see
// isStale), and each only while it still holds the record judged stale (see removeIfUnchanged). A claim or update of
// a run that still lives on this host, or one recorded on another host, is left untouched; so is a symbolic link or
// anything else that is not a regular file in a leftover's place, which one line names with its remedy. Once this run
// holds the lock, every claim names a lock instance already gone (nobody claims a live lock), and an update is written
// only by the run whose token it carries, so no removal can cost a live lock. The lock's directories are checked
// first (see assertInsideCheckout); when they no longer all belong to this checkout, one line says why and nothing is
// removed. A file that cannot be read or removed is reported and left for the next run.
async function removeLockLeftovers() {
  const lockDir = path.dirname(generationLock);
  const lockName = path.basename(generationLock);
  let names;
  try {
    await assertInsideCheckout(generationLock);
    names = await fs.readdir(lockDir);
  } catch (error) {
    process.stderr.write(`could not list the files beside ${toRepoRelative(generationLock)}: ${errorMessage(error)}\n`);
    return;
  }
  for (const name of names) {
    if (name.startsWith(`${lockName}.claim-`) || (name.startsWith(`${lockName}.`) && name.endsWith('.next'))) {
      await removeLockLeftover(path.join(lockDir, name));
    }
  }
}

// Removes one leftover beside the lock when it is a regular file whose record is stale, as removeLockLeftovers
// describes. A leftover that is not a regular file, or that cannot be examined, read or removed, is left and reported
// on one line.
async function removeLockLeftover(leftover) {
  try {
    await assertInsideCheckout(leftover);
    // Examined before it is opened, so that a link is reported rather than refused, and a FIFO is never opened.
    const stats = await fs.lstat(leftover);
    if (!stats.isFile()) {
      const kind = stats.isSymbolicLink() ? 'a symbolic link' : 'not a regular file';
      process.stderr.write(
        `${toRepoRelative(leftover)} is ${kind}; leftovers of the OpenAPI generation lock are removed only as ` +
          'regular files of this checkout, so it is left in place: delete it\n',
      );
      return;
    }
    const holder = await readLockRecord(leftover);
    if (holder !== null && isStale(holder)) {
      await removeIfUnchanged(leftover, holder);
    }
  } catch (error) {
    // Gone since the listing: nothing is left to remove.
    if (error.code === 'ENOENT') {
      return;
    }
    process.stderr.write(`could not remove ${toRepoRelative(leftover)}: ${errorMessage(error)}\n`);
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

// Releases a lock or claim file this process holds, for the process 'exit' event and for finishInterruption, so it is
// synchronous and never throws. The file is read through readLockRecordSync, which checks its directories and refuses
// a link in its place, and is removed only while it carries the given token. A lock whose recorded child still runs
// is kept, because that child goes on writing the checkout's outputs; it turns stale when the child ends, and the next
// run removes it then. A claim file, which records no child, is released the same way. A file that cannot be released,
// a refusal included, is left for the next run, which waits for it or removes it as stale. With report, a failed
// release and a lock kept for its child are each reported on one line. The 'exit' handler passes no report: it finds
// a lock to release only after removeFresh(dir) or finishInterruption could not release it, which they have reported,
// or as the process ends on an error that Node reports itself, so its line would only repeat or trail that report.
function releaseLockFileSync(token, file = generationLock, report = undefined) {
  const subject = `the OpenAPI generation lock${file === generationLock ? '' : ' claim'} ${toRepoRelative(file)}`;
  try {
    const current = readLockRecordSync(file);
    // Gone, or not this process's to release.
    if (current?.record?.token !== token) {
      return;
    }
    if (isChildRunning(current.record)) {
      report?.(
        `kept ${subject}: its child process ${current.record.childPid} still runs, and the next run removes the ` +
          'lock once that process has ended\n',
      );
      return;
    }
    unlinkSync(file);
  } catch (error) {
    // A file or directory gone meanwhile leaves nothing to release.
    if (error.code !== 'ENOENT') {
      report?.(
        `could not release ${subject} (${errorMessage(error)}); if no api:generate or lint run of this checkout is ` +
          'active, delete that file\n',
      );
    }
  }
}

// Rewrites the held lock's record with childPid set to the given pid, or without childPid when it is undefined. The
// new record is written whole to `<lock>.<token>.next` and renamed over the lock, so a reader sees the old record or
// the new one and never a partial one; the lock must still carry this run's token, or the update fails. The lock's
// directories are checked first (see assertInsideCheckoutSync), the update file is created exclusively, and the lock is
// read without following a link (see readLockRecordSync), so the update never reads or writes outside this checkout.
// The update file's name carries this run's random token, so a file already there is not this run's: it fails the
// update and is left alone, and only an update file this run created is removed again. Every step is synchronous, so
// an interruption never finds an update under way that could put the lock, or its update file, back after
// finishInterruption has released it.
function recordLockChild(lock, childPid) {
  const { childPid: previous, ...record } = lock.record;
  if (childPid !== undefined) {
    record.childPid = childPid;
  }
  const update = `${generationLock}.${lock.token}.next`;
  let created = false;
  try {
    assertInsideCheckoutSync(update);
    if (!createExclusive(update, record)) {
      throw new Error(`${toRepoRelative(update)} already exists`);
    }
    created = true;
    const current = readLockRecordSync(generationLock);
    if (current?.record?.token !== lock.token) {
      throw new Error(
        current === null ? 'the lock file no longer exists' : `the lock is now held by ${describeHolder(current)}`,
      );
    }
    renameReplacing(update, generationLock);
  } catch (error) {
    if (created) {
      try {
        assertInsideCheckoutSync(update);
        rmSync(update, { force: true });
      } catch (cleanupError) {
        if (cleanupError.code !== 'ENOENT') {
          process.stderr.write(`could not clean up ${toRepoRelative(update)}: ${errorMessage(cleanupError)}\n`);
        }
      }
    }
    const change =
      childPid === undefined ? `clear child process ${previous} from` : `record child process ${childPid} in`;
    throw new GenerationError(
      `could not ${change} the OpenAPI generation lock ${toRepoRelative(generationLock)} (${errorMessage(error)})`,
    );
  }
  lock.record = record;
}

// Renames a file over an existing one, synchronously. Windows refuses the replacement while another process has the
// target open, which waiting runs do for an instant on each poll, so those refusals are retried a few times after a
// pause that blocks this thread rather than yielding to a signal handler.
function renameReplacing(source, target) {
  const pause = new Int32Array(new SharedArrayBuffer(4));
  for (let attempt = 1; ; attempt += 1) {
    try {
      renameSync(source, target);
      return;
    } catch (error) {
      if (attempt >= 5 || !['EPERM', 'EACCES', 'EBUSY'].includes(error.code)) {
        throw error;
      }
    }
    Atomics.wait(pause, 0, 0, 50);
  }
}

function newLockRecord() {
  return { pid: process.pid, hostname: os.hostname(), token: randomUUID(), acquiredAt: new Date().toISOString() };
}

// Creates the file only if it does not exist and writes the record into it. Returns false when the file exists, a
// symbolic link included, which the exclusive open neither follows nor replaces. A record that cannot be written
// completely is removed again, so it never stays behind as a lock. All of it is synchronous, so an interruption finds
// the file either absent or complete and in its creator's hands, never being created by an operation that could
// complete after the interruption has released everything this process holds.
function createExclusive(file, record) {
  let descriptor;
  try {
    descriptor = openSync(file, 'wx');
  } catch (error) {
    if (error.code === 'EEXIST') {
      return false;
    }
    throw error;
  }
  let failure;
  try {
    writeFileSync(descriptor, `${JSON.stringify(record)}\n`);
  } catch (error) {
    failure = error;
  }
  try {
    closeSync(descriptor);
  } catch (error) {
    failure ??= error;
  }
  if (failure !== undefined) {
    try {
      rmSync(file, { force: true });
    } catch (error) {
      process.stderr.write(`could not clean up ${toRepoRelative(file)}: ${errorMessage(error)}\n`);
    }
    throw failure;
  }
  return true;
}

// Opening a lock, claim or record-update file never follows a symbolic link in its place: O_NOFOLLOW makes the open
// fail instead, with ELOOP, or EMLINK on some BSDs. Windows defines no O_NOFOLLOW, so there the open may follow a
// link, and the path is examined once the file is open instead (see assertOpenedLockFile).
const lockReadFlags = fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0);
const lockOpenFollowsLinks = fs.constants.O_NOFOLLOW === undefined;

// Reads a lock, claim or record-update file as { record, identity, mtimeMs }, where record is null while the file is
// empty or holds no valid record, or returns null when the file, or a directory it is reached through, does not
// exist. identity names this file instance in a form that can be part of a file name. The file's directories are
// checked first (see assertInsideCheckout), and a symbolic link in the file's place is refused.
async function readLockRecord(file) {
  try {
    await assertInsideCheckout(file);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
  let handle;
  try {
    handle = await fs.open(file, lockReadFlags);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw lockOpenError(file, error);
  }
  try {
    const stats = await handle.stat();
    if (lockOpenFollowsLinks) {
      let pathStats;
      try {
        pathStats = await fs.lstat(file);
      } catch (error) {
        if (error.code === 'ENOENT') {
          return null;
        }
        throw error;
      }
      assertOpenedLockFile(file, stats, pathStats);
    }
    return lockFileInstance(stats, await handle.readFile('utf8'));
  } finally {
    await handle.close();
  }
}

// readLockRecord in one synchronous step, for the lock operations that must not yield to a signal handler (see
// recordLockChild and releaseLockFileSync): the same checks, the same refusals and the same result.
function readLockRecordSync(file) {
  try {
    assertInsideCheckoutSync(file);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw error;
  }
  let descriptor;
  try {
    descriptor = openSync(file, lockReadFlags);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return null;
    }
    throw lockOpenError(file, error);
  }
  try {
    const stats = fstatSync(descriptor);
    if (lockOpenFollowsLinks) {
      let pathStats;
      try {
        pathStats = lstatSync(file);
      } catch (error) {
        if (error.code === 'ENOENT') {
          return null;
        }
        throw error;
      }
      assertOpenedLockFile(file, stats, pathStats);
    }
    return lockFileInstance(stats, readFileSync(descriptor, 'utf8'));
  } finally {
    closeSync(descriptor);
  }
}

// The error a failed open of an existing lock file raises: a symbolic link in its place, which the open refuses, is
// reported as such, and any other failure is passed on unchanged.
function lockOpenError(file, error) {
  return error.code === 'ELOOP' || error.code === 'EMLINK' ? lockLinkRefusal(file) : error;
}

// Where the open may have followed a link, the path itself, examined without following links, must be the file that
// was opened: no symbolic link, and the opened file's device and inode. Anything else is refused as a link.
function assertOpenedLockFile(file, opened, pathStats) {
  if (pathStats.isSymbolicLink() || pathStats.dev !== opened.dev || pathStats.ino !== opened.ino) {
    throw lockLinkRefusal(file);
  }
}

function lockLinkRefusal(file) {
  return new GenerationError(
    `${toRepoRelative(file)} is a symbolic link; the OpenAPI generation lock, its claims and its record updates are ` +
      'read only as regular files of this checkout, so delete it and run again',
  );
}

function lockFileInstance(stats, text) {
  const identity = `${stats.dev}-${stats.ino}-${stats.mtimeMs}-${stats.size}`;
  return { record: parseLockRecord(text), identity, mtimeMs: stats.mtimeMs };
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
  const [state] = procStatFields(stat);
  return state !== 'Z' && state !== 'X';
}

// The fields of a /proc/<pid>/stat text from the state on: state, ppid, pgrp and the rest, space-separated. They follow
// the command name, which is in parentheses and may itself contain ') ', so they start after the last ')'.
function procStatFields(stat) {
  return stat.slice(stat.lastIndexOf(')') + 2).split(' ');
}

// Removes the file while it still holds the expected holder: the same token, or, for a record that was never
// readable, the same file unchanged. Returns whether it removed the file. The read immediately before the removal
// checks the file's directories (see readLockRecord), so a refusal stops the removal, and a release fails with it.
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
// a target that is not a regular file, such as a symbolic link, is refused while staging. Every directory between the
// checkout and each target must be a directory of this checkout, never a symbolic link or junction (see
// assertInsideCheckout): a missing one is created on its own, and they are checked before each staged file is created,
// again once everything is staged, before the second rename, and before each rollback step and each removal of a staged
// file, so no copy, backup, replacement or removal leaves the checkout. Each directory check runs immediately before
// the operation it guards, but Node's fs has no directory-relative operations, so the two are separate lookups of the
// same path and a directory replaced between them by a concurrent writer of the checkout is not detected. Each fresh
// file is then renamed over its target, which replaces the target whole. When the second rename fails, or its target's
// directories no longer pass the check, the first target is put back as it was (its previous bytes, or no file) before
// the error is raised. The staged files are removed on every path, so nothing is left beside the committed pair, with
// one exception: when putting the first target back fails, its staged copy of the previous bytes is the only one left,
// so it is kept and the error names it. A staged file that cannot be removed, because its directories no longer pass
// the check or otherwise, is named as left behind.
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
  // Every name a copy is started for, until it is renamed or kept (see forgetStaged), so that cleanup touches only the
  // files this run may have created and still leaves beside the targets.
  const stagedFiles = [];
  let failed = true;
  try {
    try {
      for (const entry of pair) {
        await assertInsideCheckout(entry.target, { createMissing: true });
        stagedFiles.push(entry.staged);
        // Byte for byte: check-api-fresh.mjs compares these files with the generator's output.
        await fs.copyFile(entry.source, entry.staged, fs.constants.COPYFILE_EXCL);
        entry.existed = await stageOriginal(entry, stagedFiles);
      }
      // A directory replaced by a link while the files were staged stops publication before either target is touched.
      for (const entry of pair) {
        await assertInsideCheckout(entry.target);
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
    forgetStaged(stagedFiles, first.staged);
    try {
      await assertInsideCheckout(second.target);
      await fs.rename(second.staged, second.target);
    } catch (error) {
      throw await restoreFirstTarget(first, second, error, stagedFiles);
    }
    forgetStaged(stagedFiles, second.staged);

    for (const entry of pair) {
      process.stdout.write(`wrote ${toRepoRelative(entry.target)}\n`);
    }
    failed = false;
  } finally {
    await cleanUpAfter(failed, 'the staged publication files', () => removeStaged(stagedFiles));
  }
}

// Opening a committed file for its backup never waits and never follows a symbolic link in its place: O_NONBLOCK
// keeps the open of a FIFO from blocking, and O_NOFOLLOW makes the open of a link fail instead, with ELOOP, or EMLINK
// on some BSDs. Windows defines no O_NOFOLLOW, so there only the identity check in stageOriginal refuses a link.
const backupReadFlags = fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0) | (fs.constants.O_NONBLOCK ?? 0);

// Copies the committed file beside itself so that a failed publication can put it back, and reports whether it
// existed. The target's directories are checked again first (see assertInsideCheckout), so the lstat, the open and
// the copy's creation all happen in a directory of this checkout. The target is examined without following links,
// because a rename replaces a symbolic link itself and a restore could not put it back: anything other than a regular
// file, a dangling link included, stops publication before either target is touched. Its bytes are read only through
// a handle opened without following a link (see backupReadFlags) on the very file lstat examined, a regular file with
// the same device and inode, so a link or another file put in its place after the lstat is refused before anything
// is read. The copy is created exclusively and given the target's permission bits, so a restored file keeps them. The
// target is examined again after the copy, so a file replaced after it was opened, whose bytes the copy then no
// longer matches, stops publication too.
async function stageOriginal(entry, stagedFiles) {
  const target = toRepoRelative(entry.target);
  const notRegularFile = (kind) =>
    new Error(`${target} is ${kind}; only a regular file is replaced, so make it one and run again`);
  const replacedWhileCopied = () => new Error(`${target} was replaced while it was being copied; run again`);
  await assertInsideCheckout(entry.target);
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
    throw notRegularFile(kind);
  }
  let source;
  try {
    source = await fs.open(entry.target, backupReadFlags);
  } catch (error) {
    if (error.code === 'ELOOP' || error.code === 'EMLINK') {
      throw notRegularFile('a symbolic link');
    }
    throw error;
  }
  try {
    const opened = await source.stat();
    if (!opened.isFile() || opened.dev !== before.dev || opened.ino !== before.ino) {
      throw replacedWhileCopied();
    }
    const bytes = await source.readFile();
    const mode = before.mode & 0o7777;
    stagedFiles.push(entry.original);
    const copy = await fs.open(entry.original, 'wx', mode);
    try {
      await copy.writeFile(bytes);
      // The umask narrows the mode open applies, so the bits are set again exactly.
      await copy.chmod(mode);
    } finally {
      await copy.close();
    }
  } finally {
    await source.close();
  }
  const after = await fs.lstat(entry.target);
  if (!after.isFile() || after.dev !== before.dev || after.ino !== before.ino) {
    throw replacedWhileCopied();
  }
  return true;
}

// Called after the first target was replaced and the second was not: puts the first back from its staged original,
// or removes it when it did not exist before, and returns the error that states what the committed pair now holds.
// The first target's directories are checked again immediately before it is removed or put back (see
// assertInsideCheckout), and a refusal fails that step like any other error, so the rollback never leaves the
// checkout. When the first cannot be put back, its staged original may be the only copy of its previous bytes, so it
// is taken out of stagedFiles, which the cleanup removes, and is examined without following links once its directories
// pass the check again. The error then names one of three outcomes: the copy is a regular file, kept there to be moved
// back over the target; the copy is gone, it or a directory above it being missing; or the copy could not be examined,
// because a directory above it is refused or for any other reason, a copy that is not a regular file included, and the
// error gives that reason and asks for every directory above it to be a directory of this checkout again before it is
// moved back, so no remedy sends a move through a link.
async function restoreFirstTarget(first, second, cause, stagedFiles) {
  const replaced = toRepoRelative(first.target);
  const unchanged = toRepoRelative(second.target);
  const reason = `publication failed: could not replace ${unchanged} (${errorMessage(cause)})`;
  const inconsistent =
    `the new ${replaced} remains beside the unchanged ${unchanged}, so the committed pair may be inconsistent`;
  if (!first.existed) {
    try {
      await removeInsideCheckout(first.target);
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
    await assertInsideCheckout(first.target);
    await fs.rename(first.original, first.target);
  } catch (restoreError) {
    forgetStaged(stagedFiles, first.original);
    const recovery = toRepoRelative(first.original);
    const failure = `${reason}, and restoring ${replaced} also failed (${errorMessage(restoreError)}); ${inconsistent}`;
    // A failed rename leaves its source in place, so the copy is looked for where it was staged, and only through
    // directories of this checkout; one reached through a link is not looked into.
    let unexamined = null;
    try {
      await assertInsideCheckout(first.original);
      if (!(await fs.lstat(first.original)).isFile()) {
        unexamined = 'it is not a regular file';
      }
    } catch (error) {
      if (error.code === 'ENOENT') {
        return new GenerationError(
          `${failure}; its staged copy ${recovery} is gone too, so run npm run api:generate again`,
        );
      }
      unexamined = errorMessage(error);
    }
    if (unexamined !== null) {
      return new GenerationError(
        `${failure}; its previous content was staged as ${recovery}, which could not be examined (${unexamined}): ` +
          'make every directory it is reached through a directory of this checkout again, and check that it is a ' +
          `regular file, before moving it back over ${replaced}, or run npm run api:generate again`,
      );
    }
    return new GenerationError(
      `${failure}; its previous content is kept in ${recovery}: move that file back over ${replaced}, or run ` +
        'npm run api:generate again',
    );
  }
  forgetStaged(stagedFiles, first.original);
  return new GenerationError(
    `${reason}; ${replaced} was restored to its previous content, so neither committed file changed`,
  );
}

// Takes a staged name out of stagedFiles once it needs no removal: it was renamed over its target, or it is a copy
// that is kept.
function forgetStaged(stagedFiles, file) {
  const index = stagedFiles.indexOf(file);
  if (index !== -1) {
    stagedFiles.splice(index, 1);
  }
}

// Removes every staged name still listed that exists. Each removal is attempted, and a failure, a refusal of the
// file's directories included, names every file left behind with its reason.
async function removeStaged(stagedFiles) {
  const leftovers = [];
  for (const file of stagedFiles) {
    try {
      await removeInsideCheckout(file);
    } catch (error) {
      leftovers.push(`${toRepoRelative(file)} (${errorMessage(error)})`);
    }
  }
  if (leftovers.length > 0) {
    throw new GenerationError(`could not remove ${leftovers.join(', ')}; delete what is left by hand`);
  }
}

// Removes the file, as fs.rm with force does, once its directories are checked again (see assertInsideCheckout), so
// the removal never leaves the checkout. A missing directory, like a missing file, leaves nothing to remove.
async function removeInsideCheckout(file) {
  try {
    await assertInsideCheckout(file);
  } catch (error) {
    if (error.code === 'ENOENT') {
      return;
    }
    throw error;
  }
  await fs.rm(file, { force: true });
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

// Runs a command without a shell, with inherited stderr and environment, stdout inherited too or, with captureStdout,
// collected and returned as text, and no stdin, which none of the commands reads. On POSIX the command starts detached,
// as the leader of a new session and process group that its descendants share, so that an interruption can stop the
// whole tree (see the Interruption section). A failed start or a failed exit becomes a GenerationError that keeps the
// command's exit status, after any captured output has been passed on to stderr. Given the held lock, the child's pid
// is recorded in the lock for as long as the child runs, so the lock outlives this process while a child goes on
// writing the checkout's outputs; a child whose pid cannot be recorded is stopped, with its descendants, before the
// error is raised. Each started process is tracked from the moment it starts until it has ended with its descendants,
// so that an interruption can stop them; once the process is interrupted, no command starts, and a command that ends
// afterwards fails the run whatever its status, since dotnet sent SIGTERM exits with 0.
async function runChecked(command, args, cwd, description, startFailureMessage, { lock, captureStdout = false } = {}) {
  throwIfInterrupted();
  let child;
  try {
    child = spawn(command, args, {
      cwd,
      stdio: ['ignore', captureStdout ? 'pipe' : 'inherit', 'inherit'],
      detached: process.platform !== 'win32',
    });
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
  // Tracked, and its pid recorded in the lock, in the same synchronous step as the start, so an interruption always
  // knows of the process and its lock always names it.
  const started = { child, closed: false };
  startedProcesses.add(started);
  exit.then(() => onStartedProcessClosed(started));
  if (lock !== undefined) {
    try {
      recordLockChild(lock, child.pid);
    } catch (error) {
      signalStartedProcess(started, 'SIGKILL');
      await exit;
      await waitUntil(() => hasEnded(started), childKillGraceMilliseconds);
      throw error;
    }
  }
  const { status, signal } = await exit;
  if (lock !== undefined) {
    await cleanUpAfter(status !== 0, toRepoRelative(generationLock), () => recordLockChild(lock, undefined));
  }
  throwIfInterrupted();
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
// writes in the default layout. Each run pins its own marker into its temporary directory, and removes this one only
// when it is an eligible leftover of this checkout: a regular file in a directory that, symbolic links resolved, lies
// inside the checkout (the canonical path generateFresh passes in). The generation lock has already refused an obj/
// that is, or is reached through, a symbolic link or junction (see acquireLockFile); here too a missing directory or
// file, a directory that resolves elsewhere, and anything other than a regular file are left untouched, and the run
// goes on.
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

// progress records whether publication began and whether it completed, which is what an interrupted run reports.
async function writeCommitted(progress) {
  const fresh = await generateFresh();
  let published = false;
  try {
    // An interrupted run publishes nothing. A publication that has begun is never cut short by an interruption, which
    // waits for it to complete or roll back by its own logic.
    throwIfInterrupted();
    progress.publishing = true;
    publicationsUnderWay += 1;
    try {
      await publishFresh(fresh);
    } finally {
      publicationsUnderWay -= 1;
    }
    published = true;
    progress.published = true;
  } finally {
    await cleanUpAfter(!published, fresh.dir, () => removeFresh(fresh.dir));
  }
}

// After an interruption, api:generate ends with one line saying so and what became of the committed pair, in place of
// a failure message: the interruption is neither a toolchain failure nor a generation result. Returns whether the run
// was interrupted.
function reportInterruptedGeneration(progress, failure) {
  const interrupted = currentInterruption();
  if (interrupted === null) {
    return false;
  }
  let outcome;
  if (!progress.publishing) {
    outcome = 'no committed file was changed';
  } else if (progress.published) {
    const cleanup = failure === undefined ? '' : `, and ${errorMessage(failure)}`;
    outcome = `its publication had begun and completed, so both committed files were written${cleanup}`;
  } else {
    outcome = `its publication had begun and failed: ${errorMessage(failure)}`;
  }
  process.stderr.write(`api:generate was interrupted by ${interrupted.signal}; ${outcome}\n`);
  process.exitCode = interrupted.exitCode;
  return true;
}

// Command line. Neither script takes an argument apart from a request for help, so a mistyped or unknown option is
// refused before anything runs, rather than ignored by a run that rewrites or rebuilds something.
const helpFlags = Object.freeze(['--help', '-h']);

/**
 * Reads a script's arguments, as process.argv.slice(2) holds them, before anything else runs. Returns undefined when
 * there are none, and the script runs. Otherwise the script runs nothing and ends with the returned exit status: 0
 * after writing usage to stdout for exactly one --help or -h, and 2 after writing one line that names the first
 * refused argument, then usage, to stderr. Every argument that is not a help flag is refused, and so is a help flag
 * given beside another argument.
 */
export function handleArguments(args, usage) {
  if (args.length === 0) {
    return undefined;
  }
  if (args.length === 1 && helpFlags.includes(args[0])) {
    process.stdout.write(usage);
    return 0;
  }
  const refused = args.find((argument) => !helpFlags.includes(argument)) ?? args[1];
  process.stderr.write(
    `unexpected argument ${JSON.stringify(refused)}: this script takes no arguments, only --help or -h on its own\n` +
      usage,
  );
  return 2;
}

/**
 * The --help text of either script: its synopsis lines, its description paragraph and its own exit statuses, around
 * the sections both share, which are the prerequisites, the rules generateFresh() enforces on the temporary
 * directory, the options, and the statuses for a refused argument and for an interruption, whose meaning
 * `interrupted` completes. Paragraphs are wrapped to usageWidth columns.
 */
export function formatUsage({ synopsis, description, exitStatuses, interrupted }) {
  const signalStatuses = interruptionSignals
    .map((signal) => `${128 + os.constants.signals[signal]} for ${signal}`)
    .join(', ');
  const statuses = [
    ...exitStatuses,
    ['2', 'an argument other than one --help or -h was given; nothing ran'],
    ['128+n', `interrupted by signal n (${signalStatuses}); ${interrupted}`],
  ];
  const width = Math.max(...statuses.map(([status]) => status.length));
  return [
    ...synopsis,
    '',
    ...wrapUsage(description, ''),
    '',
    'Prerequisites:',
    ...wrapUsage('`npm ci` run in web/, and the .NET SDK 10.0.1xx (see the root README.md).', '  '),
    ...wrapUsage('No database and no connection string are needed.', '  '),
    '',
    'Temporary directory:',
    ...temporaryDirectoryRules(),
    '',
    'Options:',
    ...wrapUsage('print this help and exit, running nothing', '  -h, --help  '),
    '',
    'Exit status:',
    ...statuses.flatMap(([status, meaning]) => wrapUsage(meaning, `  ${status.padEnd(width)}  `)),
    '',
  ].join('\n');
}

const usageWidth = 80;

// Breaks a paragraph at spaces into lines of at most usageWidth columns where its words allow. The first line starts
// with the given prefix, and every later line is indented to the prefix's width.
function wrapUsage(text, prefix) {
  const indent = ' '.repeat(prefix.length);
  const lines = [];
  let line = prefix;
  for (const word of text.split(' ')) {
    if (line.length > indent.length && line.length + 1 + word.length > usageWidth) {
      lines.push(line);
      line = indent;
    }
    line += line.length > indent.length ? ` ${word}` : word;
  }
  lines.push(line);
  return lines;
}

// The temporary-directory rules of generateFresh(), as the help states them. They are built from the character sets
// that generateFresh() checks, so the help cannot drift from the checks: the variable as written is held to
// shellUnquoted, and the directory os.tmpdir() resolves to is held to shellInDoubleQuotes, which shellUnquoted
// contains, and to urlText. A path free of both sets therefore always passes. Returns the section's lines, with the
// characters on a line of their own.
function temporaryDirectoryRules() {
  const characters = [...new Set(`${shellUnquoted.characters}${urlText.characters}`)]
    .map((character) => (character === ' ' ? 'space' : character))
    .join(' ');
  const windows = process.platform === 'win32';
  const variables = windows ? 'TMP and TEMP, when set, must be absolute paths' : 'TMPDIR, when set, must be absolute';
  return [
    ...wrapUsage('Each run writes its files to a new directory under os.tmpdir() and removes it when it ends.', '  '),
    ...wrapUsage(
      `${variables}. Neither ${windows ? 'they' : 'it'} nor the directory os.tmpdir() resolves to with symbolic ` +
        `links followed${windows ? '' : ' (/tmp when TMPDIR is unset)'} may hold a control character or any of ` +
        'these characters:',
      '  ',
    ),
    `    ${characters}`,
  ];
}

const generateUsage = () =>
  formatUsage({
    synopsis: ['Usage: node scripts/api-generate.mjs [--help | -h]', '   or: npm run api:generate   (from web/)'],
    description:
      `Regenerates the committed OpenAPI document ${toRepoRelative(COMMITTED.json)} and the client types ` +
      `generated from it, ${toRepoRelative(COMMITTED.ts)}. It builds src/EpiInfo.Api with document generation ` +
      'forced on and runs openapi-typescript on the result, both in a new temporary directory, then replaces the ' +
      'two committed files together. Run it from any directory after a change to the API contract, then commit ' +
      'both files.',
    exitStatuses: [
      ['0', 'both committed files were written'],
      ['1', 'generation or publication failed; a failing dotnet or openapi-typescript run gives its own status'],
    ],
    interrupted:
      'removing the temporary directory and releasing the lock are attempted, a failure is reported, and no ' +
      'committed file is changed unless their publication was already under way, which then completes or rolls ' +
      'back as a whole',
  });

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
  // The arguments are read before anything else, so --help and a refused argument run nothing.
  const status = handleArguments(process.argv.slice(2), generateUsage());
  if (status !== undefined) {
    process.exitCode = status;
  } else {
    const progress = { publishing: false, published: false };
    try {
      await writeCommitted(progress);
      reportInterruptedGeneration(progress);
    } catch (error) {
      if (!reportInterruptedGeneration(progress, error)) {
        const message = error instanceof Error ? error.message : String(error);
        process.stderr.write(`api:generate failed: ${message}\n`);
        process.exitCode = error instanceof GenerationError ? error.exitCode : 1;
      }
    }
  }
}
