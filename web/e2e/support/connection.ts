/**
 * Test connection resolution for the Playwright smoke test, the TypeScript counterpart of
 * `tests/Shared/TestDatabase.cs`. Both resolve the same variables with the same precedence, so the API
 * that Playwright starts and the .NET integration tests reach the same rehearsal database.
 *
 * `playwright.config.ts` imports this module even when `E2E_BASE_URL` names an external API and no
 * database settings exist, so importing it has no side effects, reads no environment variable and never
 * throws. Only types come from `mssql`; the driver itself is loaded by `sql.ts`. Nothing is written to the
 * console, and no message carries a value taken from the environment or from a connection string.
 */
import type { config as MssqlConfig } from 'mssql';

/** Environment variable holding a complete connection string; it wins whenever it is set and not blank. */
export const CONNECTION_STRING_VARIABLE = 'ConnectionStrings__EpiInfo';

/**
 * Environment variable naming the server. It is used as the data source as given (trimmed), so it may
 * carry a port as `host,port`; a bare host means port 1433 to both SqlClient and `mssql`.
 */
export const SQL_HOST_VARIABLE = 'SQL_HOST';

/** Environment variable holding the `sa` password of the rehearsal server. */
export const PASSWORD_VARIABLE = 'MSSQL_SA_PASSWORD';

/** The database the fallback connection string names. */
export const DATABASE_NAME = 'EpiRehearsal';

/** The data source used when {@link SQL_HOST_VARIABLE} is not set. */
export const DEFAULT_SERVER = 'localhost,1433';

const LOGIN = 'sa';
const DEFAULT_PORT = 1433;
// Matches the five-second probe of TestDatabase.EnsureReachableAsync.
const CONNECTION_TIMEOUT_MS = 5_000;
const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
const DEFAULT_POOL_MAX = 2;

const GUIDANCE =
  `Set ${CONNECTION_STRING_VARIABLE}, or ${PASSWORD_VARIABLE} (and optionally ${SQL_HOST_VARIABLE}, ` +
  `default ${DEFAULT_SERVER}), to reach the ${DATABASE_NAME} rehearsal database; see testdata/README.md.`;

/**
 * The rehearsal database is not configured, or its connection string cannot be used. The smoke test lets
 * it propagate, so the run fails rather than skips. Its message names variables and keys, never a value.
 */
export class TestDatabaseConfigurationError extends Error {
  /** @param message What is missing or wrong, followed by how to provide the database. */
  constructor(message: string) {
    super(message);
    this.name = 'TestDatabaseConfigurationError';
  }
}

/** Optional pool and timeout settings for {@link toMssqlConfig}. */
export interface MssqlConfigOptions {
  /** Maximum connections in the pool, a positive integer (default 2). */
  readonly poolMax?: number;
  /** Request timeout in milliseconds, a non-negative integer where 0 disables it (default 30000). */
  readonly requestTimeoutMs?: number;
}

/**
 * Resolves the connection string of the rehearsal database, exactly as `TestDatabase.ConnectionString`
 * does: {@link CONNECTION_STRING_VARIABLE} as given when it is not blank; otherwise a string in the form
 * `testdata/README.md` documents, naming server {@link SQL_HOST_VARIABLE} (default
 * {@link DEFAULT_SERVER}), database {@link DATABASE_NAME}, login `sa` with the
 * {@link PASSWORD_VARIABLE} password, and `TrustServerCertificate=True`. Every value comes from the
 * environment; a server or password holding `;`, a quote or edge whitespace is quoted, so both SqlClient
 * and {@link toMssqlConfig} read it back unchanged.
 *
 * @param env The environment to read, `process.env` by default.
 * @returns The connection string. It holds the password, so callers must never print it.
 * @throws {TestDatabaseConfigurationError} Neither {@link CONNECTION_STRING_VARIABLE} nor
 * {@link PASSWORD_VARIABLE} is set.
 */
export function resolveConnectionString(env: NodeJS.ProcessEnv = process.env): string {
  const connectionString = nonBlank(env[CONNECTION_STRING_VARIABLE]);
  if (connectionString !== undefined) {
    return connectionString;
  }

  const password = nonBlank(env[PASSWORD_VARIABLE]);
  if (password === undefined) {
    throw new TestDatabaseConfigurationError(
      withGuidance(`Neither ${CONNECTION_STRING_VARIABLE} nor ${PASSWORD_VARIABLE} is set.`),
    );
  }

  const server = nonBlank(env[SQL_HOST_VARIABLE])?.trim() ?? DEFAULT_SERVER;
  return (
    `Server=${quoteConnectionValue(server)};Database=${DATABASE_NAME};User Id=${LOGIN};` +
    `Password=${quoteConnectionValue(password)};TrustServerCertificate=True`
  );
}

/**
 * Whether the environment names the database explicitly: {@link CONNECTION_STRING_VARIABLE} is set, or
 * both {@link SQL_HOST_VARIABLE} and {@link PASSWORD_VARIABLE} are. With `E2E_BASE_URL` set, the smoke
 * test requires this before it runs, because it must verify the database the external API uses rather
 * than fall back to the default server. Blank values count as unset. It never throws.
 *
 * @param env The environment to read, `process.env` by default.
 */
export function hasDatabaseSettings(env: NodeJS.ProcessEnv = process.env): boolean {
  if (nonBlank(env[CONNECTION_STRING_VARIABLE]) !== undefined) {
    return true;
  }
  return nonBlank(env[SQL_HOST_VARIABLE]) !== undefined && nonBlank(env[PASSWORD_VARIABLE]) !== undefined;
}

/**
 * Converts an ADO.NET-style SQL Server connection string, such as the one
 * {@link resolveConnectionString} returns, into an `mssql` pool configuration.
 *
 * Recognised keys, case-insensitive and with whitespace ignored: `Server` (`Data Source`, `Address`, `Addr`,
 * `Network Address`), `Database` (`Initial Catalog`), `User Id` (`UID`, `User`), `Password` (`PWD`),
 * `TrustServerCertificate` and `Encrypt`; other keys are ignored, and the last occurrence of a key wins.
 * The string is read as `tests/Shared/TestDatabase.cs` reads it through `SqlConnectionStringBuilder`:
 * - whitespace is the .NET `char.IsWhiteSpace` set, U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680,
 *   U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000, so U+0085 is whitespace and U+FEFF is not;
 * - an unquoted empty value such as a trailing `Encrypt=` leaves its key unset and clears an earlier
 *   occurrence or alias, while a quoted empty value (`Encrypt=""`) is an empty value and is invalid;
 * - every non-empty `Encrypt` or `TrustServerCertificate` occurrence, under any alias, must be valid, even
 *   one that a later occurrence replaces or clears;
 * - `TrustServerCertificate` takes `true`, `yes`, `false` or `no`, case-insensitive, with edge whitespace
 *   ignored; `Encrypt` takes `true`, `yes` or `Mandatory`, `false`, `no` or `Optional`, and `Strict`,
 *   case-insensitive and untrimmed. Neither takes `1` or `0`, which SqlClient rejects.
 *
 * The server may carry a `tcp:` prefix and a `,port` (default 1433) or a `\instance` name. Encryption is on
 * and the server certificate is verified unless the string says otherwise.
 *
 * The connect timeout is five seconds, matching the probe of `TestDatabase.EnsureReachableAsync`.
 * `useUTC` is deliberately left at the `mssql` default (`true`): the UTC fields of a returned `Date` then
 * equal the stored `datetime` wall-clock value, so callers compare a date-only value exactly with
 * `value.toISOString().slice(0, 10)`.
 *
 * @param connectionString The connection string to convert.
 * @param options Pool size and request timeout; defaults are 2 connections and 30 seconds.
 * @returns The configuration for `new sql.ConnectionPool(…)`.
 * @throws {TestDatabaseConfigurationError} The string is malformed, lacks the server, database, user or
 * password, or holds an invalid port, `Encrypt` or `TrustServerCertificate` value. The message names the
 * key only.
 * @throws {RangeError} An option is outside its documented range.
 */
export function toMssqlConfig(connectionString: string, options?: MssqlConfigOptions): MssqlConfig {
  const poolMax = options?.poolMax ?? DEFAULT_POOL_MAX;
  const requestTimeout = options?.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
  if (!Number.isInteger(poolMax) || poolMax < 1) {
    throw new RangeError('poolMax must be a positive integer.');
  }
  if (!Number.isInteger(requestTimeout) || requestTimeout < 0) {
    throw new RangeError('requestTimeoutMs must be a non-negative integer.');
  }

  const settings = parseConnectionString(connectionString);
  const target = parseServer(requireSetting(settings, 'server'));
  const trustValue = settings.get('trustServerCertificate');
  const encryptValue = settings.get('encrypt');

  return {
    server: target.host,
    ...(target.port === undefined ? {} : { port: target.port }),
    database: requireSetting(settings, 'database'),
    user: requireSetting(settings, 'user'),
    password: requireSetting(settings, 'password'),
    options: {
      encrypt: encryptValue === undefined ? true : parseEncrypt(encryptValue),
      trustServerCertificate:
        trustValue === undefined ? false : parseBoolean(trustValue, SETTING_NAMES.trustServerCertificate),
      ...(target.instanceName === undefined ? {} : { instanceName: target.instanceName }),
    },
    connectionTimeout: CONNECTION_TIMEOUT_MS,
    requestTimeout,
    pool: { max: poolMax, min: 0 },
  };
}

type SettingKey = 'server' | 'database' | 'user' | 'password' | 'trustServerCertificate' | 'encrypt';

// Keys are matched lower-cased with whitespace removed, so `Data Source` and `datasource` are one key.
const SETTING_KEYS: ReadonlyMap<string, SettingKey> = new Map<string, SettingKey>([
  ['server', 'server'],
  ['datasource', 'server'],
  ['address', 'server'],
  ['addr', 'server'],
  ['networkaddress', 'server'],
  ['database', 'database'],
  ['initialcatalog', 'database'],
  ['userid', 'user'],
  ['uid', 'user'],
  ['user', 'user'],
  ['password', 'password'],
  ['pwd', 'password'],
  ['trustservercertificate', 'trustServerCertificate'],
  ['encrypt', 'encrypt'],
]);

// The key names messages use; a message never repeats a value.
const SETTING_NAMES: Readonly<Record<SettingKey, string>> = {
  server: 'Server',
  database: 'Database',
  user: 'User Id',
  password: 'Password',
  trustServerCertificate: 'TrustServerCertificate',
  encrypt: 'Encrypt',
};

// SqlClient reads these data sources as the local machine.
const LOCAL_SERVER_ALIASES: ReadonlySet<string> = new Set(['.', '(local)']);

interface ServerTarget {
  readonly host: string;
  readonly port?: number;
  readonly instanceName?: string;
}

function withGuidance(problem: string): string {
  return `${problem} ${GUIDANCE}`;
}

/** The value as given when it holds a non-whitespace character, otherwise `undefined`. */
function nonBlank(value: string | undefined): string | undefined {
  return value === undefined || value.trim() === '' ? undefined : value;
}

// The whitespace of .NET `char.IsWhiteSpace` and `string.Trim`, which SqlClient's connection-string parser
// uses: U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and
// U+3000. Unlike JavaScript's `\s` and `String.prototype.trim`, it includes U+0085 and excludes U+FEFF.
const DOTNET_WHITESPACE = /^[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]$/;

/** Whether one UTF-16 code unit is .NET whitespace ({@link DOTNET_WHITESPACE}). */
function isWhitespace(character: string): boolean {
  return DOTNET_WHITESPACE.test(character);
}

/** The text without leading and trailing .NET whitespace, as `string.Trim()` returns it. */
function trimWhitespace(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && isWhitespace(text.charAt(start))) {
    start += 1;
  }
  while (end > start && isWhitespace(text.charAt(end - 1))) {
    end -= 1;
  }
  return text.slice(start, end);
}

/** Double-quotes a value that `;`, a quote or edge whitespace would otherwise break, doubling inner `"`. */
function quoteConnectionValue(value: string): string {
  const needsQuotes = /[;'"]/.test(value) || value !== trimWhitespace(value);
  return needsQuotes ? `"${value.replaceAll('"', '""')}"` : value;
}

function malformed(): TestDatabaseConfigurationError {
  return new TestDatabaseConfigurationError(
    withGuidance('The connection string is malformed: every setting must have the form key=value.'),
  );
}

/**
 * Splits a connection string into its recognised settings, following the ADO.NET rules: `;` separates
 * settings outside quotes, a key ends at its first `=`, unquoted keys and values are trimmed, and a value
 * opened by `"` or `'` runs to the matching quote, where a doubled quote is a literal one. Whitespace is the
 * .NET set SqlClient uses ({@link DOTNET_WHITESPACE}): it includes U+0085 and excludes U+FEFF, unlike
 * JavaScript's `\s`. The last occurrence of a key or any of its aliases wins. An unquoted value that is empty
 * or only whitespace means the setting is unset, as `SqlConnectionStringBuilder` reads it: the key is
 * removed, clearing any earlier occurrence or alias, so its default applies. A quoted empty value (`""` or
 * `''`) is kept as `''`. Like `SqlConnectionStringBuilder`, which converts every occurrence as it reads it,
 * each non-empty `Encrypt` or `TrustServerCertificate` occurrence, quoted or not and under any alias, must
 * be valid on its own: an invalid one throws even when a later occurrence or alias replaces or clears it.
 *
 * @throws {TestDatabaseConfigurationError} The string is malformed, or an `Encrypt` or
 * `TrustServerCertificate` occurrence is invalid. The message names the key only.
 */
function parseConnectionString(connectionString: string): Map<SettingKey, string> {
  const settings = new Map<SettingKey, string>();
  const length = connectionString.length;
  let index = 0;

  while (index < length) {
    const character = connectionString.charAt(index);
    if (character === ';' || isWhitespace(character)) {
      index += 1;
      continue;
    }

    const equals = connectionString.indexOf('=', index);
    const separator = connectionString.indexOf(';', index);
    if (equals === -1 || (separator !== -1 && separator < equals)) {
      throw malformed();
    }
    const key = connectionString
      .slice(index, equals)
      .split('')
      .filter((keyCharacter) => !isWhitespace(keyCharacter))
      .join('')
      .toLowerCase();
    if (key === '') {
      throw malformed();
    }

    index = equals + 1;
    while (index < length && isWhitespace(connectionString.charAt(index))) {
      index += 1;
    }

    // `undefined` marks an unquoted empty value, which SqlClient reads as no value at all.
    let value: string | undefined;
    const quote = connectionString.charAt(index);
    if (quote === '"' || quote === "'") {
      [value, index] = readQuotedValue(connectionString, index + 1, quote);
      while (index < length && isWhitespace(connectionString.charAt(index))) {
        index += 1;
      }
      if (index < length && connectionString.charAt(index) !== ';') {
        throw malformed();
      }
    } else {
      const end = connectionString.indexOf(';', index);
      const stop = end === -1 ? length : end;
      const unquoted = trimWhitespace(connectionString.slice(index, stop));
      value = unquoted === '' ? undefined : unquoted;
      index = stop;
    }

    const settingKey = SETTING_KEYS.get(key);
    if (settingKey !== undefined) {
      if (value === undefined) {
        settings.delete(settingKey);
      } else {
        validateOccurrence(settingKey, value);
        settings.set(settingKey, value);
      }
    }
  }

  return settings;
}

/** Reads a quoted value whose opening quote precedes `start`; returns it with the index after its closing quote. */
function readQuotedValue(text: string, start: number, quote: string): [string, number] {
  let value = '';
  let index = start;
  while (index < text.length) {
    const character = text.charAt(index);
    if (character !== quote) {
      value += character;
      index += 1;
    } else if (text.charAt(index + 1) === quote) {
      value += quote;
      index += 2;
    } else {
      return [value, index + 1];
    }
  }
  throw new TestDatabaseConfigurationError(
    withGuidance('The connection string has an unterminated quoted value.'),
  );
}

function requireSetting(settings: ReadonlyMap<SettingKey, string>, key: SettingKey): string {
  const value = settings.get(key);
  if (value === undefined || value.trim() === '') {
    throw new TestDatabaseConfigurationError(withGuidance(`The connection string has no ${SETTING_NAMES[key]}.`));
  }
  return value;
}

/**
 * Reads a data source: an optional `tcp:` prefix, then `host`, `host,port` or `host\instance`. When both a
 * port and an instance are given the port wins, as in SqlClient, so the instance name is dropped.
 */
function parseServer(value: string): ServerTarget {
  const invalid = (): TestDatabaseConfigurationError =>
    new TestDatabaseConfigurationError(
      withGuidance(`The connection string has an invalid ${SETTING_NAMES.server} value.`),
    );

  let rest = value.trim();
  if (rest.toLowerCase().startsWith('tcp:')) {
    rest = rest.slice('tcp:'.length).trim();
  }

  let portText: string | undefined;
  const comma = rest.indexOf(',');
  if (comma !== -1) {
    portText = rest.slice(comma + 1).trim();
    rest = rest.slice(0, comma).trim();
  }

  let instanceName: string | undefined;
  const backslash = rest.indexOf('\\');
  if (backslash !== -1) {
    instanceName = rest.slice(backslash + 1).trim();
    rest = rest.slice(0, backslash).trim();
    if (instanceName === '') {
      throw invalid();
    }
  }

  if (rest === '') {
    throw invalid();
  }
  const host = LOCAL_SERVER_ALIASES.has(rest.toLowerCase()) ? 'localhost' : rest;

  if (portText !== undefined) {
    const port = /^\d{1,5}$/.test(portText) ? Number(portText) : Number.NaN;
    if (!(port >= 1 && port <= 65_535)) {
      throw invalid();
    }
    return { host, port };
  }
  return instanceName === undefined ? { host, port: DEFAULT_PORT } : { host, instanceName };
}

/**
 * Checks one non-empty occurrence of a setting that `SqlConnectionStringBuilder` converts as it reads it,
 * so an invalid occurrence fails even when a later occurrence or alias replaces or clears it.
 */
function validateOccurrence(key: SettingKey, value: string): void {
  if (key === 'encrypt') {
    parseEncrypt(value);
  } else if (key === 'trustServerCertificate') {
    parseBoolean(value, SETTING_NAMES.trustServerCertificate);
  }
}

function invalidValue(keyName: string): TestDatabaseConfigurationError {
  return new TestDatabaseConfigurationError(withGuidance(`The connection string has an invalid ${keyName} value.`));
}

// SqlClient's boolean spellings, case-insensitive once .NET whitespace is trimmed; it rejects `1` and `0`.
function parseBoolean(value: string, keyName: string): boolean {
  switch (trimWhitespace(value).toLowerCase()) {
    case 'true':
    case 'yes':
      return true;
    case 'false':
    case 'no':
      return false;
    default:
      throw invalidValue(keyName);
  }
}

/**
 * `Encrypt` takes the spellings of SqlClient 5's `SqlConnectionEncryptOption`: `true`, `yes` or `Mandatory`;
 * `false`, `no` or `Optional`; and `Strict` (TDS 8.0). Like SqlClient under the invariant culture, it
 * matches them case-insensitively but untrimmed, so a quoted value with edge whitespace is invalid, as are
 * `1` and `0`.
 */
function parseEncrypt(value: string): boolean | 'strict' {
  switch (value.toLowerCase()) {
    case 'true':
    case 'yes':
    case 'mandatory':
      return true;
    case 'false':
    case 'no':
    case 'optional':
      return false;
    case 'strict':
      return 'strict';
    default:
      throw invalidValue(SETTING_NAMES.encrypt);
  }
}
