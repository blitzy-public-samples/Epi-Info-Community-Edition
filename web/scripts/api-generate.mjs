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

import { spawnSync } from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { URL, fileURLToPath, pathToFileURL } from 'node:url';

// Resolved from this file, never from process.cwd(), so the script works from any working directory.
const webDir = fileURLToPath(new URL('..', import.meta.url));
const repoRoot = path.resolve(webDir, '..');
const apiDir = path.join(repoRoot, 'src', 'EpiInfo.Api');
const apiProject = path.join(apiDir, 'EpiInfo.Api.csproj');
const openApiCacheMarker = path.join(apiDir, 'obj', 'EpiInfo.Api.OpenApiFiles.cache');
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
 * temporary directory. Returns { json, ts, dir }: the fresh document, the fresh client types and the directory that
 * holds them, which the caller removes with removeFresh(dir). A failed run removes the directory itself.
 */
export async function generateFresh() {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'epiinfo-openapi-'));
  try {
    // The generator target is incremental on this marker: while it exists, an up-to-date tree neither regenerates
    // the document nor writes it to a new directory. Removing it forces a run whose output is byte-identical.
    await fs.rm(openApiCacheMarker, { force: true });

    // cwd is the repository root so the SDK resolves the root global.json. The build restores and recompiles first
    // when sources changed, so the generator never reads a stale assembly.
    runChecked(
      'dotnet',
      [
        'build',
        apiProject,
        '-p:OpenApiGenerateDocumentsOnBuild=true',
        `-p:OpenApiDocumentsDirectory=${dir}`,
        '--nologo',
      ],
      repoRoot,
      `dotnet build ${toRepoRelative(apiProject)}`,
      (error) =>
        `the dotnet command could not be started (${error.code ?? error.message}); ` +
        'the .NET SDK 10.0.1xx is required, see the root README.md',
    );

    const json = path.join(dir, documentFileName);
    if (!(await isFile(json))) {
      const found = await fs.readdir(dir);
      throw new GenerationError(
        `OpenAPI generation produced no ${documentFileName} in ${dir}; found: ${found.length > 0 ? found.join(', ') : 'nothing'}`,
      );
    }

    // Relative names with the temporary directory as cwd: the CLI resolves its arguments as URLs against
    // file://<cwd>/, which misreads an absolute Windows path as a URL scheme, and an empty cwd guarantees that no
    // redocly.yaml changes the output. No flags, so the result is the CLI's default output.
    const cli = await openapiTypescriptCli();
    runChecked(
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
    await removeFresh(dir);
    throw error;
  }
}

/** Removes a directory returned by generateFresh(). */
export async function removeFresh(dir) {
  await fs.rm(dir, { recursive: true, force: true });
}

// Runs a command with inherited stdio and environment, and turns a failed start or a failed exit into a
// GenerationError that keeps the command's exit status.
function runChecked(command, args, cwd, description, startFailureMessage) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit' });
  if (result.error) {
    throw new GenerationError(startFailureMessage(result.error));
  }
  if (result.status !== 0) {
    const reason = result.status === null ? `was terminated by signal ${result.signal}` : `exited with code ${result.status}`;
    throw new GenerationError(`${description} ${reason}`, result.status ?? 1);
  }
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
  try {
    for (const [source, target] of [
      [fresh.json, COMMITTED.json],
      [fresh.ts, COMMITTED.ts],
    ]) {
      await fs.mkdir(path.dirname(target), { recursive: true });
      // Byte for byte: check-api-fresh.mjs compares these files with the generator's output.
      await fs.copyFile(source, target);
      process.stdout.write(`wrote ${toRepoRelative(target)}\n`);
    }
  } finally {
    await removeFresh(fresh.dir);
  }
}

// Importing this module, as check-api-fresh.mjs does, must not rewrite the committed files.
const isMain = process.argv[1] !== undefined && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;

if (isMain) {
  try {
    await writeCommitted();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`api:generate failed: ${message}\n`);
    process.exitCode = error instanceof GenerationError ? error.exitCode : 1;
  }
}
