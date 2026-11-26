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
// run removes it as stale.
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
    releaseLockFileSync(lock.token);
  }
  for (const [claim, token] of heldClaims) {
    releaseLockFileSync(token, claim);
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
  await fs.mkdir(path.dirname(generationLock), { recursive: true });
  const record = newLockRecord();
  const deadline = Date.now() + lockWaitLimitMilliseconds;
  let announced = false;
  for (;;) {
    // An interrupted run stops waiting at the next poll.
    throwIfInterrupted();
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
// because that child goes on writing the checkout's outputs; it turns stale when the child ends. A claim file, which
// records no child, is released the same way.
function releaseLockFileSync(token, file = generationLock) {
  try {
    const record = parseLockRecord(readFileSync(file, 'utf8'));
    if (record?.token === token && !isChildRunning(record)) {
      unlinkSync(file);
    }
  } catch {
    // Nothing more can be done while the process exits.
  }
}

// Rewrites the held lock's record with childPid set to the given pid, or without childPid when it is undefined. The
// new record is written whole to `<lock>.<token>.next` and renamed over the lock, so a reader sees the old record or
// the new one and never a partial one; the lock must still carry this run's token, or the update fails. Every step is
// synchronous, so an interruption never finds an update under way that could put the lock, or its update file, back
// after finishInterruption has released it.
function recordLockChild(lock, childPid) {
  const { childPid: previous, ...record } = lock.record;
  if (childPid !== undefined) {
    record.childPid = childPid;
  }
  const update = `${generationLock}.${lock.token}.next`;
  try {
    writeFileSync(update, `${JSON.stringify(record)}\n`);
    let current = null;
    try {
      current = { record: parseLockRecord(readFileSync(generationLock, 'utf8')) };
    } catch (error) {
      if (error.code !== 'ENOENT') {
        throw error;
      }
    }
    if (current?.record?.token !== lock.token) {
      throw new Error(
        current === null ? 'the lock file no longer exists' : `the lock is now held by ${describeHolder(current)}`,
      );
    }
    renameReplacing(update, generationLock);
  } catch (error) {
    try {
      rmSync(update, { force: true });
    } catch (cleanupError) {
      process.stderr.write(`could not clean up ${toRepoRelative(update)}: ${errorMessage(cleanupError)}\n`);
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

// Creates the file only if it does not exist and writes the record into it. Returns false when the file exists. A
// record that cannot be written completely is removed again, so it never stays behind as a lock. All of it is
// synchronous, so an interruption finds the file either absent or complete and in its creator's hands, never being
// created by an operation that could complete after the interruption has released everything this process holds.
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
  const [state] = procStatFields(stat);
  return state !== 'Z' && state !== 'X';
}

// The fields of a /proc/<pid>/stat text from the state on: state, ppid, pgrp and the rest, space-separated. They follow
// the command name, which is in parentheses and may itself contain ') ', so they start after the last ')'.
function procStatFields(stat) {
  return stat.slice(stat.lastIndexOf(')') + 2).split(' ');
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
      'the temporary directory is removed, and no committed file is changed unless their publication was ' +
      'already under way, which then completes or rolls back as a whole',
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
