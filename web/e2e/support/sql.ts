/**
 * Read-only SQL verification and the exclusive test lock for the Playwright smoke test
 * (`web/e2e/smoke.spec.ts`).
 *
 * Through SQL the smoke test only reads and holds the database-wide test lock, so this module sends nothing but
 * `SELECT` statements, `sp_getapplock` and `sp_releaseapplock`, and a transaction begin and rollback. Every
 * value is a typed parameter. Every table name comes from the fixed seeded names below, the tables
 * `tests/Shared/SqlSnapshot.cs` reads, and is checked at runtime before it is quoted, so a caller's string never
 * reaches the command text.
 *
 * Importing the module opens no connection: each pool belongs to the function that creates it. The connection
 * is resolved by `connection.ts`, exactly as the .NET tests resolve it. Nothing is written to the console, and
 * no message carries a connection string, a host, a password or a driver message.
 *
 * A driver failure, from connecting, a verification query, the lock calls or closing a pool, is reported as a
 * new `Error` with fixed text naming the part (SQL verifier or test lock), the operation, and either the SQL
 * Server error number or a driver code from a fixed list of `mssql` and `tedious` codes. The driver's message,
 * its original and preceding errors and its server name are never copied, and the driver's error is never
 * attached as the cause, because any of them can name the host, the database or the statement. Errors this
 * module raises from its own checks, such as a negative `sp_getapplock` return code, and the
 * `TestDatabaseConfigurationError` of `connection.ts`, propagate unchanged.
 */
import sql from 'mssql';
import type { ConnectionPool, IResult, Request, Transaction } from 'mssql';

import {
  CONNECTION_STRING_VARIABLE,
  DATABASE_NAME,
  PASSWORD_VARIABLE,
  SQL_HOST_VARIABLE,
  resolveConnectionString,
  toMssqlConfig,
  type MssqlConfigOptions,
} from './connection';

/**
 * The application lock resource. It equals `DatabaseWriteLock.ResourceName` in
 * `tests/Shared/DatabaseWriteLock.cs`, so the smoke test and every .NET writer or shared-lock reader exclude
 * each other.
 */
export const TEST_LOCK_RESOURCE = 'EpiInfo.Web.Tests';

/** A seeded form, named by its base table. */
export type SeededForm = 'CaseReport' | 'FoodHistory';

/** A page table of a seeded form, named `<form><PageId>`. */
export type PageTable = 'CaseReport1' | 'FoodHistory2' | 'FoodHistory3';

/** The page tables of each seeded form, in page order. */
export const PAGE_TABLES: Readonly<Record<SeededForm, readonly PageTable[]>> = Object.freeze({
  CaseReport: frozenTables('CaseReport1'),
  FoodHistory: frozenTables('FoodHistory2', 'FoodHistory3'),
});

/**
 * The page of each form that holds `LastName` (`nvarchar(4000)`), the field the smoke test puts its marker in.
 * `FoodHistory3` has no `LastName`.
 */
export const MARKER_PAGE_TABLE: Readonly<Record<SeededForm, PageTable>> = Object.freeze({
  CaseReport: 'CaseReport1',
  FoodHistory: 'FoodHistory2',
});

/**
 * One column value as `mssql` returns it: `string` for `nvarchar` and `ntext`, `number` for `int`, `smallint`,
 * `tinyint` and `float`, `boolean` for `bit`, `Date` for `datetime`, and `null` for a database null.
 */
export type SqlCell = string | number | boolean | Date | null;

/**
 * Every column of one page-table row, keyed by the column name as the table defines it. A `datetime` value is
 * a `Date` whose UTC fields equal the stored wall-clock value (see {@link BaseRow}).
 */
export type PageRow = Readonly<Record<string, SqlCell>>;

/**
 * The base-table row of a record: `UniqueKey int`, `RecStatus smallint`, `GlobalRecordId nvarchar(255)`, the
 * four save stamps (`nvarchar(255)` logon names, `datetime` times) and `FKEY nvarchar(255)`.
 *
 * The driver keeps its default `useUTC: true`, so the UTC fields of a returned `Date` equal the stored
 * `datetime` wall-clock value: compare a stamp exactly with `value.toISOString()`, never with local-time
 * accessors.
 */
export interface BaseRow {
  uniqueKey: number;
  recStatus: number;
  globalRecordId: string;
  firstSaveLogonName: string | null;
  firstSaveTime: Date | null;
  lastSaveLogonName: string | null;
  lastSaveTime: Date | null;
  fkey: string | null;
}

/** Read-only queries against the seeded forms, on a pool of their own. */
export interface SqlVerifier {
  /**
   * Counts the base rows of `form`: the active set `RecStatus = 1`, or every row when `includeDeleted` is
   * true, the two sets the API's `count` reports.
   */
  countRecords(form: SeededForm, includeDeleted: boolean): Promise<number>;

  /** The base row with this `GlobalRecordId`, or `null` when there is none. */
  baseRowById(form: SeededForm, globalRecordId: string): Promise<BaseRow | null>;

  /**
   * The base rows whose `LastName` on {@link MARKER_PAGE_TABLE} equals `marker` exactly, in `UniqueKey` order.
   * A base row without that page row is not returned.
   */
  baseRowsByMarker(form: SeededForm, marker: string): Promise<BaseRow[]>;

  /** Every column of the page row with this `GlobalRecordId`, or `null` when there is none. */
  pageRow(table: PageTable, globalRecordId: string): Promise<PageRow | null>;

  /** Closes the pool. Later calls return the first call's promise. */
  close(): Promise<void>;
}

/** The held test lock. */
export interface ExclusiveTestLock {
  /**
   * Releases the lock, rolls its transaction back and closes its pool, then rethrows the first failure of
   * those steps. Every step runs even when an earlier one fails. Later calls return the first call's promise.
   * A driver failure is reported as fixed text naming the step and the SQL Server error number or allow-listed
   * driver code only; a negative `sp_releaseapplock` return code is reported with that code.
   */
  release(): Promise<void>;
}

// Names each pool's session in sys.dm_exec_sessions.program_name, beside the .NET lock's "EpiInfo.Web.Tests lock".
const VERIFIER_APPLICATION_NAME = 'EpiInfo.Web.Tests e2e verifier';
const LOCK_APPLICATION_NAME = 'EpiInfo.Web.Tests e2e lock';

// The lock wait matches DatabaseWriteLock. The request timeout stays above it, or the driver would cancel a
// legitimate wait before sp_getapplock answers.
const LOCK_TIMEOUT_SECONDS = 300;
const LOCK_REQUEST_TIMEOUT_MS = 330_000;

const RESOURCE_PARAMETER = 'resource';
// sp_getapplock and sp_releaseapplock declare @Resource as nvarchar(255).
const RESOURCE_LENGTH = 255;
const GLOBAL_RECORD_ID_LENGTH = 255;
const LAST_NAME_LENGTH = 4000;

const ACQUIRE_STATEMENT =
  'DECLARE @rc int; ' +
  "EXEC @rc = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', " +
  '@LockTimeout = 300000; ' +
  'SELECT @rc AS rc;';

const RELEASE_STATEMENT =
  "DECLARE @rc int; EXEC @rc = sp_releaseapplock @Resource = @resource, @LockOwner = N'Transaction'; " +
  'SELECT @rc AS rc;';

// One select list for both base-row queries, so they alias every column identically.
const BASE_ROW_COLUMNS =
  'b.[UniqueKey] AS uniqueKey, b.[RecStatus] AS recStatus, b.[GlobalRecordId] AS globalRecordId, ' +
  'b.[FirstSaveLogonName] AS firstSaveLogonName, b.[FirstSaveTime] AS firstSaveTime, ' +
  'b.[LastSaveLogonName] AS lastSaveLogonName, b.[LastSaveTime] AS lastSaveTime, b.[FKEY] AS fkey';

// mssql codes for a call the library refused before it reached the server, which a fresh request may avoid.
const LIBRARY_USAGE_CODES: ReadonlySet<string> = new Set(['EREQINPROG', 'ENOCONN', 'EARGS', 'EINJECT']);

// mssql codes for a transaction that no longer exists: never begun, or rolled back by the server. Either way the
// server holds no transaction-owned lock for it.
const ENDED_TRANSACTION_CODES: ReadonlySet<string> = new Set(['ENOTBEGUN', 'EABORT']);

// The parts of this module a driver failure message names.
const VERIFIER_PART = 'SQL verifier';
const LOCK_PART = 'test lock';

// Every error code mssql 12.8.1 and tedious 20.4.3 assign, and the codes the mssql typings declare. A failure
// message names a code only when it is in this list, so it never carries text the driver or the OS produced.
const DRIVER_ERROR_CODES: ReadonlySet<string> = new Set([
  'ABORT_ERR',
  'EABORT',
  'EALREADYBEGUN',
  'EALREADYCONNECTED',
  'EALREADYCONNECTING',
  'EALREADYPREPARED',
  'EARGS',
  'ECANCEL',
  'ECLOSE',
  'ECLOSING',
  'ECONNCLOSED',
  'ECONNECTING',
  'ECONNRESET',
  'EDEPRECATED',
  'EDRIVER',
  'EDUPEPARAM',
  'EENCRYPT',
  'EFEDAUTH',
  'EINJECT',
  'EINSTLOOKUP',
  'EINTERFACENOTSUPP',
  'EINVALIDSTATE',
  'EJSON',
  'ELOGIN',
  'ENAME',
  'ENOCONN',
  'ENOTBEGUN',
  'ENOTOPEN',
  'ENOTPREPARED',
  'EPARAM',
  'EREQINPROG',
  'EREQUEST',
  'ESOCKET',
  'ETDS',
  'ETIMEOUT',
]);

const NO_DRIVER_CODE = 'no driver error code';

const SEEDED_FORMS: readonly string[] = Object.keys(PAGE_TABLES);
const PAGE_TABLE_NAMES: ReadonlySet<string> = new Set(Object.values(PAGE_TABLES).flat());

/**
 * Opens a pool of its own for read-only verification queries; it never shares the lock's pool. Close it with
 * {@link SqlVerifier.close}.
 *
 * When the driver fails, a method of the returned verifier rejects with an `Error` whose fixed text names the
 * operation and the SQL Server error number or allow-listed driver code only, never the caller's id or marker.
 *
 * @throws {TestDatabaseConfigurationError} The database is not configured, or its connection string cannot be
 * used; thrown by `connection.ts` unchanged.
 * @throws {Error} The database cannot be reached. The message names the configuration variables and the
 * allow-listed driver error code only.
 */
export async function openSqlVerifier(): Promise<SqlVerifier> {
  const pool = createPool(VERIFIER_APPLICATION_NAME);
  await connectPool(pool, VERIFIER_PART);
  let closing: Promise<void> | undefined;

  return {
    async countRecords(form: SeededForm, includeDeleted: boolean): Promise<number> {
      assertSeededForm(form);
      const filter = includeDeleted ? '' : ' WHERE [RecStatus] = 1';
      const result = await driverCall(VERIFIER_PART, `counting the records of ${form}`, () =>
        pool.request().query<{ n: number }>(`SELECT COUNT(*) AS n FROM ${quoteTable(form)}${filter};`),
      );
      const row = result.recordset.at(0);
      if (row === undefined) {
        throw new Error(`The record count of ${form} returned no row.`);
      }
      return row.n;
    },

    async baseRowById(form: SeededForm, globalRecordId: string): Promise<BaseRow | null> {
      assertSeededForm(form);
      const result = await driverCall(VERIFIER_PART, `reading a base row of ${form}`, () =>
        pool
          .request()
          .input('id', sql.NVarChar(GLOBAL_RECORD_ID_LENGTH), globalRecordId)
          .query<BaseRow>(`SELECT ${BASE_ROW_COLUMNS} FROM ${quoteTable(form)} b WHERE b.[GlobalRecordId] = @id;`),
      );
      return result.recordset.at(0) ?? null;
    },

    async baseRowsByMarker(form: SeededForm, marker: string): Promise<BaseRow[]> {
      assertSeededForm(form);
      const markerPage = MARKER_PAGE_TABLE[form];
      assertPageTable(markerPage);
      const result = await driverCall(VERIFIER_PART, `finding the marked base rows of ${form}`, () =>
        pool
          .request()
          .input('marker', sql.NVarChar(LAST_NAME_LENGTH), marker)
          .query<BaseRow>(
            `SELECT ${BASE_ROW_COLUMNS} FROM ${quoteTable(form)} b ` +
              `INNER JOIN ${quoteTable(markerPage)} p ON p.[GlobalRecordId] = b.[GlobalRecordId] ` +
              'WHERE p.[LastName] = @marker ORDER BY b.[UniqueKey];',
          ),
      );
      return [...result.recordset];
    },

    async pageRow(table: PageTable, globalRecordId: string): Promise<PageRow | null> {
      assertPageTable(table);
      const result = await driverCall(VERIFIER_PART, `reading a page row of ${table}`, () =>
        pool
          .request()
          .input('id', sql.NVarChar(GLOBAL_RECORD_ID_LENGTH), globalRecordId)
          .query<Record<string, SqlCell>>(`SELECT * FROM ${quoteTable(table)} WHERE [GlobalRecordId] = @id;`),
      );
      const row = result.recordset.at(0);
      return row === undefined ? null : Object.freeze({ ...row });
    },

    close(): Promise<void> {
      closing ??= driverCall(VERIFIER_PART, 'closing the verifier pool', () => pool.close());
      return closing;
    },
  };
}

/**
 * Takes the database-wide test lock {@link TEST_LOCK_RESOURCE} exclusively, waiting up to 300 seconds for every
 * other holder, .NET exclusive or shared, to release it.
 *
 * The lock is owned by a transaction on a dedicated pool of one connection, and every lock call runs through a
 * request bound to that transaction, so all of them run on one session. A crashed test process ends that
 * session, and the server then rolls the transaction back and releases the lock, so the lock never outlives
 * the run. The lock changes no data.
 *
 * @throws {TestDatabaseConfigurationError} The database is not configured, or its connection string cannot be
 * used; thrown by `connection.ts` unchanged.
 * @throws {Error} The database cannot be reached (the message names the configuration variables and the
 * allow-listed driver error code only); beginning the transaction or calling `sp_getapplock` failed in the driver (the
 * message names the operation and the SQL Server error number or allow-listed driver code only); or
 * `sp_getapplock` did not grant the lock (the message names its return code and meaning). The transaction is
 * rolled back and the pool closed before any of them is thrown.
 */
export async function acquireExclusiveTestLock(): Promise<ExclusiveTestLock> {
  const pool = createPool(LOCK_APPLICATION_NAME, { poolMax: 1, requestTimeoutMs: LOCK_REQUEST_TIMEOUT_MS });
  await connectPool(pool, LOCK_PART);

  const transaction = new sql.Transaction(pool);
  let begun = false;
  try {
    await driverCall(LOCK_PART, 'beginning the lock transaction', () => transaction.begin());
    begun = true;
    const request = await driverCall(LOCK_PART, 'preparing the sp_getapplock request', () =>
      lockRequest(transaction),
    );
    const code = selectedReturnCode(
      await driverCall(LOCK_PART, 'requesting sp_getapplock', () => runLockStatement(request, ACQUIRE_STATEMENT)),
    );
    if (code === undefined) {
      throw missingReturnCode('sp_getapplock');
    }
    if (code < 0) {
      throw new Error(
        `The exclusive test lock '${TEST_LOCK_RESOURCE}' was not granted: sp_getapplock returned ${code} ` +
          `(${describeAcquireCode(code)}).`,
      );
    }
    return lockHandle(pool, transaction, request);
  } catch (err) {
    if (begun) {
      await settleQuietly(() => transaction.rollback());
    }
    await settleQuietly(() => pool.close());
    // Every driver call above goes through driverCall, so what arrives here is fixed text: a reduced driver
    // failure or one of this function's own checks.
    throw err;
  }
}

function frozenTables(...tables: PageTable[]): readonly PageTable[] {
  return Object.freeze(tables);
}

function assertSeededForm(form: string): asserts form is SeededForm {
  if (!Object.hasOwn(PAGE_TABLES, form)) {
    throw new Error(`unknown seeded table: the seeded forms are ${SEEDED_FORMS.join(', ')}.`);
  }
}

function assertPageTable(table: string): asserts table is PageTable {
  if (!PAGE_TABLE_NAMES.has(table)) {
    throw new Error(`unknown seeded table: the seeded page tables are ${[...PAGE_TABLE_NAMES].join(', ')}.`);
  }
}

/** `[dbo].[name]` with any `]` doubled; called only with a name a guard has accepted. */
function quoteTable(name: SeededForm | PageTable): string {
  return `[dbo].[${name.replaceAll(']', ']]')}]`;
}

function createPool(applicationName: string, options?: MssqlConfigOptions): ConnectionPool {
  const config = toMssqlConfig(resolveConnectionString(), options);
  const pool = new sql.ConnectionPool({ ...config, options: { ...config.options, appName: applicationName } });
  // The pool also emits 'error' for failures it reports through the rejected connect or query. An EventEmitter
  // without a listener rethrows that event as an uncaught exception, which would end the test worker with a
  // driver message that can name the host.
  pool.on('error', ignoreReportedPoolError);
  return pool;
}

function ignoreReportedPoolError(): void {
  // Deliberately no action: the operation that failed rejects with the same failure, and the caller reports it.
}

async function connectPool(pool: ConnectionPool, purpose: string): Promise<void> {
  try {
    await pool.connect();
  } catch (err) {
    await settleQuietly(() => pool.close());
    // Only an allow-listed code: the driver's message can name the host, so it is neither repeated nor attached
    // as the cause.
    const code = listedDriverCode(err) ?? NO_DRIVER_CODE;
    throw new Error(
      `The ${DATABASE_NAME} test database cannot be reached for the ${purpose} (${code}). ` +
        `Set ${CONNECTION_STRING_VARIABLE}, or ${SQL_HOST_VARIABLE} and ${PASSWORD_VARIABLE}, to the rehearsal ` +
        'database; see testdata/README.md.',
    );
  }
}

/** A request bound to the lock's transaction, and so to its one session, carrying the resource parameter. */
function lockRequest(transaction: Transaction): Request {
  const request = new sql.Request(transaction);
  request.input(RESOURCE_PARAMETER, sql.NVarChar(RESOURCE_LENGTH), TEST_LOCK_RESOURCE);
  return request;
}

/** The rows a lock statement selects: one row holding the procedure's return code as `rc`. */
type LockStatementResult = IResult<{ rc: number }>;

/**
 * Runs a lock statement. A driver failure rejects with the driver's own error, so the caller can classify it
 * before it reduces it to fixed text with {@link driverFailure}.
 */
async function runLockStatement(request: Request, statement: string): Promise<LockStatementResult> {
  return await request.query<{ rc: number }>(statement);
}

/** The procedure's return code that a lock statement selected, or `undefined` when it selected none. */
function selectedReturnCode(result: LockStatementResult): number | undefined {
  const code = result.recordset.at(0)?.rc;
  return typeof code === 'number' && Number.isInteger(code) ? code : undefined;
}

function missingReturnCode(procedure: string): Error {
  return new Error(`${procedure} completed without a return code.`);
}

function lockHandle(pool: ConnectionPool, transaction: Transaction, request: Request): ExclusiveTestLock {
  let releasing: Promise<void> | undefined;
  return {
    release(): Promise<void> {
      releasing ??= releaseLock(pool, transaction, request);
      return releasing;
    },
  };
}

async function releaseLock(pool: ConnectionPool, transaction: Transaction, request: Request): Promise<void> {
  // Each step runs whatever the earlier ones did; the first failure is rethrown once all have run. A driver
  // failure is classified on the driver's own error and recorded only as fixed text.
  let releaseFailure: Error | undefined;
  let releaseDriverError: unknown;
  try {
    const code = selectedReturnCode(await callRelease(transaction, request));
    if (code === undefined) {
      releaseFailure = missingReturnCode('sp_releaseapplock');
    } else if (code < 0) {
      releaseFailure = new Error(
        `Releasing the test lock '${TEST_LOCK_RESOURCE}' failed: sp_releaseapplock returned ${code}.`,
      );
    }
  } catch (err) {
    releaseDriverError = err;
    releaseFailure = driverFailure(LOCK_PART, 'calling sp_releaseapplock', err);
  }

  let rollbackFailure: Error | undefined;
  let transactionEnded = false;
  try {
    await transaction.rollback();
  } catch (err) {
    if (isEndedTransaction(err)) {
      transactionEnded = true;
    } else {
      rollbackFailure = driverFailure(LOCK_PART, 'rolling back the lock transaction', err);
    }
  }

  let closeFailure: Error | undefined;
  try {
    await pool.close();
  } catch (err) {
    closeFailure = driverFailure(LOCK_PART, 'closing the lock pool', err);
  }

  // A transaction the server already ended released its transaction-owned lock with it, so a release call that
  // failed only because that transaction no longer exists left nothing held.
  if (transactionEnded && isEndedTransaction(releaseDriverError)) {
    releaseFailure = undefined;
  }

  const failure = releaseFailure ?? rollbackFailure ?? closeFailure;
  if (failure !== undefined) {
    throw failure;
  }
}

/**
 * Calls `sp_releaseapplock` through the request that took the lock. When the library refuses to run that
 * request again, it retries once with a fresh request bound to the same transaction, and so to the same session.
 * A driver failure rejects with the driver's own error, which {@link releaseLock} classifies before it reduces
 * it to fixed text.
 */
async function callRelease(transaction: Transaction, request: Request): Promise<LockStatementResult> {
  try {
    return await runLockStatement(request, RELEASE_STATEMENT);
  } catch (err) {
    const code = errorCode(err);
    if (code === undefined || !LIBRARY_USAGE_CODES.has(code)) {
      throw err;
    }
  }
  return await runLockStatement(lockRequest(transaction), RELEASE_STATEMENT);
}

function isEndedTransaction(err: unknown): boolean {
  const code = errorCode(err);
  return err instanceof sql.TransactionError && code !== undefined && ENDED_TRANSACTION_CODES.has(code);
}

function describeAcquireCode(code: number): string {
  switch (code) {
    case -1:
      return (
        `the request timed out after ${LOCK_TIMEOUT_SECONDS} seconds; another test run holds ` +
        `'${TEST_LOCK_RESOURCE}'`
      );
    case -2:
      return 'the request was cancelled';
    case -3:
      return 'the request was chosen as a deadlock victim';
    case -999:
      return 'a parameter validation or other call error';
    default:
      return 'an undocumented return code';
  }
}

/** The `code` of a driver error, such as `ELOGIN`, `ESOCKET` or `ETIMEOUT`; `undefined` for anything else. */
function errorCode(err: unknown): string | undefined {
  return err instanceof Error && 'code' in err && typeof err.code === 'string' ? err.code : undefined;
}

/**
 * Runs one driver call, turning its failure into {@link driverFailure}. Only driver calls are wrapped, so the
 * errors this module raises from its own checks keep their text.
 */
async function driverCall<T>(part: string, operation: string, call: () => T | Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (err) {
    throw driverFailure(part, operation, err);
  }
}

/**
 * A driver failure as fixed text: the module part, the operation and {@link safeDriverCode}. `operation` names
 * only fixed objects, such as a seeded table, never a caller's value. Nothing of `err` is kept: its message,
 * `originalError`, `precedingErrors` and `serverName` can name the host, the database or the statement, so none
 * is copied and `err` is not attached as the cause.
 */
function driverFailure(part: string, operation: string, err: unknown): Error {
  return new Error(`The ${part} failed while ${operation} (${safeDriverCode(err)}).`);
}

/**
 * `SQL Server error <number>` for an `mssql` error carrying a server error number; otherwise the driver code when
 * {@link DRIVER_ERROR_CODES} lists it; otherwise fixed text.
 */
function safeDriverCode(err: unknown): string {
  if (!(err instanceof Error)) {
    return 'a thrown value that is not an Error';
  }
  if (err instanceof sql.MSSQLError && 'number' in err) {
    const serverNumber: unknown = err.number;
    if (typeof serverNumber === 'number' && Number.isInteger(serverNumber) && serverNumber > 0) {
      return `SQL Server error ${serverNumber}`;
    }
  }
  return listedDriverCode(err) ?? NO_DRIVER_CODE;
}

/** The driver code of `err` when {@link DRIVER_ERROR_CODES} lists it, otherwise `undefined`. */
function listedDriverCode(err: unknown): string | undefined {
  const code = errorCode(err);
  return code !== undefined && DRIVER_ERROR_CODES.has(code) ? code : undefined;
}

/** Runs a cleanup step whose failure must not replace the failure the caller is already reporting. */
async function settleQuietly(step: () => Promise<void>): Promise<void> {
  try {
    await step();
  } catch {
    // Deliberately ignored: the caller rethrows the original failure, which explains the run's outcome.
  }
}
