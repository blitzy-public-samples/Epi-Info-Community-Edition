// Vite build, development server and Vitest configuration for the web client.
//
// No JSX plugin: Vite's built-in esbuild compiles TSX with the automatic JSX runtime,
// matching tsconfig.json "jsx": "react-jsx", so @vitejs/plugin-react is not needed.
import { lookup } from 'node:dns/promises';
import type { EventEmitter } from 'node:events';
import type { ClientRequest, OutgoingHttpHeaders } from 'node:http';
import { IncomingMessage, ServerResponse, request } from 'node:http';
import { Http2ServerRequest, Http2ServerResponse, constants as http2Constants } from 'node:http2';
import { isIP } from 'node:net';
import { fileURLToPath } from 'node:url';
import type { Connect, HttpServer, Logger, Plugin, ResolvedConfig, ViteDevServer } from 'vite';
import { defineConfig } from 'vitest/config';

// The API listens only on the IPv4 loopback address (Kestrel:Endpoints:Http:Url in
// src/EpiInfo.Api/appsettings.json). The proxy names 127.0.0.1 literally, never
// localhost, which can resolve to ::1 where nothing listens.
const apiOrigin = 'http://127.0.0.1:5000';
const apiUrl = new URL(apiOrigin);

// A request whose URL starts with one of these goes to the API, as Vite's proxy matched.
const apiPrefixes = ['/api', '/health'];

// Hop-by-hop headers describe one connection and are never relayed (RFC 9110 7.6.1),
// nor is any header the Connection header names. HTTP2-Settings belongs to one connection's
// h2c upgrade (RFC 7540 3.2.1), and Node refuses it in an HTTP/2 response.
const hopByHopHeaders = [
  'connection',
  'keep-alive',
  'proxy-connection',
  'te',
  'trailer',
  'upgrade',
  'http2-settings',
];

const unavailableText = `The API at ${apiOrigin} is not answering.\n`;

// How long a request body waits for the API's 100 Continue before it is sent anyway, as a
// client may do rather than wait indefinitely (RFC 9110 10.1.1).
const continueWaitMs = 1000;

/**
 * The lower-case names of the headers not relayed: the hop-by-hop ones and those the
 * Connection header value `connection` names. A request keeps Content-Length and
 * Transfer-Encoding whatever Connection names, since they frame the body piped after them
 * (Node re-chunks a chunked body). A response drops Transfer-Encoding, because Node frames
 * the relayed body for the client itself.
 */
function droppedHeaders(connection: string | undefined, isRequest: boolean): Set<string> {
  const dropped = new Set(hopByHopHeaders);
  for (const token of (connection ?? '').split(',')) {
    dropped.add(token.trim().toLowerCase());
  }
  if (isRequest) {
    dropped.delete('content-length');
    dropped.delete('transfer-encoding');
  } else {
    dropped.add('transfer-encoding');
  }
  return dropped;
}

/**
 * The end-to-end headers of `raw`, a list in `rawHeaders` form (name, value, name, …), as
 * droppedHeaders selects them.
 */
function endToEndHeaders(
  raw: readonly string[],
  connection: string | undefined,
  isRequest: boolean,
): string[] {
  const dropped = droppedHeaders(connection, isRequest);

  const kept: string[] = [];
  for (let i = 0; i + 1 < raw.length; i += 2) {
    if (!dropped.has(raw[i].toLowerCase())) {
      kept.push(raw[i], raw[i + 1]);
    }
  }
  return kept;
}

/** The hosts Vite allows besides `allowedHosts` (its getAdditionalAllowedHosts). */
function additionalAllowedHosts({ server, preview }: ResolvedConfig): string[] {
  const hosts: string[] = [];
  if (typeof server.host === 'string' && server.host) {
    hosts.push(server.host);
  }
  if (typeof server.hmr === 'object' && server.hmr.host) {
    hosts.push(server.hmr.host);
  }
  if (typeof preview.host === 'string' && preview.host) {
    hosts.push(preview.host);
  }
  if (server.origin && URL.canParse(server.origin)) {
    hosts.push(new URL(server.origin).hostname);
  }
  return hosts;
}

/**
 * Vite's DNS-rebinding guard (its isHostAllowed), mirrored here because the forwarder runs
 * ahead of the Vite middleware that guarded the built-in proxy: IP literals, localhost and
 * its subdomains, the additional hosts, and `allowedHosts` entries, where a leading dot
 * also admits every subdomain.
 */
function isHostAllowed(
  host: string | undefined,
  allowedHosts: readonly string[],
  additionalHosts: readonly string[],
): boolean {
  if (!host) {
    return false;
  }
  if (/^(?:file|.+-extension):/i.test(host)) {
    return true;
  }

  const trimmed = host.trim();
  if (trimmed.startsWith('[')) {
    const end = trimmed.indexOf(']');
    return end >= 0 && isIP(trimmed.slice(1, end)) === 6;
  }

  const colon = trimmed.indexOf(':');
  const hostname = colon === -1 ? trimmed : trimmed.slice(0, colon);
  return (
    isIP(hostname) === 4 ||
    hostname === 'localhost' ||
    hostname.endsWith('.localhost') ||
    additionalHosts.includes(hostname) ||
    allowedHosts.some(
      (allowed) =>
        allowed === hostname ||
        (allowed.startsWith('.') && (allowed.slice(1) === hostname || hostname.endsWith(allowed))),
    )
  );
}

/**
 * The log suffix for a failure: only an errno-style code such as ECONNREFUSED, because an
 * error's message and stack can carry request values.
 */
function errorCode(error: unknown): string {
  const code = error instanceof Error && 'code' in error ? error.code : undefined;
  return typeof code === 'string' && /^E[A-Z0-9_]+$/.test(code) ? ` (${code})` : '';
}

/**
 * A request and its response as a server Vite owns delivers them: node:http's classes for
 * HTTP/1.x, on every such server, and node:http2's compatibility classes for HTTP/2, which
 * the TLS server Vite creates for server.https or preview.https without a proxy also takes.
 */
type HttpRequest = IncomingMessage | Http2ServerRequest;
type HttpResponse = ServerResponse | Http2ServerResponse;

/**
 * The headers of the HTTP/1.1 request to the API for `req`. Host stays as the client sent it,
 * as Vite's proxy kept it without changeOrigin; a request without one gets the API's host.
 * An HTTP/1 request keeps its end-to-end header lines as sent. An HTTP/2 request carries its
 * method, path, scheme and authority as ':'-prefixed pseudo-headers, which have no HTTP/1.1
 * form: the method and path go on the request line, and the authority becomes Host when the
 * client sent no Host (RFC 9113 8.3.1). Its other headers come from Node's header object,
 * which joins split Cookie fields with "; " as RFC 9113 8.2.3 requires before HTTP/1.1.
 */
function upstreamRequestHeaders(req: HttpRequest): string[] {
  if (!(req instanceof Http2ServerRequest)) {
    const headers = endToEndHeaders(req.rawHeaders, req.headers.connection, true);
    if (req.headers.host === undefined) {
      headers.push('Host', apiUrl.host);
    }
    return headers;
  }

  const fields: string[] = [];
  for (const [name, value] of Object.entries(req.headers)) {
    if (name.startsWith(':') || value === undefined) {
      continue;
    }
    for (const item of Array.isArray(value) ? value : [value]) {
      fields.push(name, item);
    }
  }
  const headers = endToEndHeaders(fields, req.headers.connection, true);
  if (req.headers.host === undefined) {
    headers.push('Host', req.headers[':authority'] ?? apiUrl.host);
  }
  return headers;
}

/**
 * Writes the API's status and end-to-end headers to `res`. HTTP/1 gets the reason phrase and
 * the header lines as the API sent them, repeated lines and name case included. HTTP/2 has
 * no reason phrase (RFC 9113 8.3.2), and Node warns when one is passed, so it gets the status
 * code and a header object: Node's parsed form of the API's headers, where Set-Cookie is an
 * array of its lines and any other repeated header is a single value, as HTTP/2 requires of
 * its single-value headers. Connection-specific headers, which Node refuses in an HTTP/2
 * response, are dropped as for HTTP/1.
 */
function relayResponseHead(res: HttpResponse, upstreamRes: IncomingMessage): void {
  const status = upstreamRes.statusCode ?? 502;
  const { connection } = upstreamRes.headers;
  if (!(res instanceof Http2ServerResponse)) {
    res.writeHead(
      status,
      upstreamRes.statusMessage,
      endToEndHeaders(upstreamRes.rawHeaders, connection, false),
    );
    return;
  }

  const dropped = droppedHeaders(connection, false);
  const headers: OutgoingHttpHeaders = {};
  for (const [name, value] of Object.entries(upstreamRes.headers)) {
    if (value !== undefined && !dropped.has(name)) {
      headers[name] = value;
    }
  }
  res.writeHead(status, headers);
}

/** Answers `res` with a fixed status, headers and body, the same way on HTTP/1 and HTTP/2. */
function respond(res: HttpResponse, status: number, headers: OutgoingHttpHeaders, body = ''): void {
  // One call per class, the same in each: the two classes declare different writeHead and
  // end overloads, so TypeScript cannot call either on their union.
  if (res instanceof Http2ServerResponse) {
    res.writeHead(status, headers).end(body);
  } else {
    res.writeHead(status, headers).end(body);
  }
}

/**
 * Cuts `res` off, so the client knows the response it may have started is incomplete. An
 * HTTP/1 response closes its connection. An HTTP/2 response is one stream of a shared
 * connection: that stream is reset with INTERNAL_ERROR. The compatibility destroy() would
 * reset it with NO_ERROR, which RFC 9113 8.1 uses for a response that is complete.
 */
function cutOff(res: HttpResponse): void {
  if (res instanceof Http2ServerResponse) {
    res.stream.close(http2Constants.NGHTTP2_INTERNAL_ERROR);
  } else {
    res.destroy();
  }
}

/**
 * Sends `req` to the API unchanged (method, URL, Host and body) and relays the API's status
 * line, headers and body. It writes no debug output and logs nothing about the request.
 */
function forwardToApi(req: HttpRequest, res: HttpResponse, logger: Logger): void {
  let upstream: ClientRequest | undefined;
  let upstreamResponse: IncomingMessage | undefined;
  // Set once the exchange is over (finished, abandoned or failed); after that nothing is
  // destroyed or logged.
  let settled = false;

  // The exchange is finished once the API's response has fully arrived or the client's
  // response has finished, even while the request body is still coming: the API may answer
  // early and close, as Kestrel's 413 for an oversized body does. The rest of the body is
  // drained, not forwarded, so the client's connection stays usable, and the API connection
  // is released. Never earlier than the response's 'end': destroying the request discards
  // response data not yet relayed.
  const finish = (): void => {
    if (settled) {
      return;
    }
    settled = true;
    if (upstream) {
      req.unpipe(upstream);
      upstream.destroy();
    }
    req.resume();
  };

  // The client went away or its request stream broke: the API exchange is dropped silently.
  const abandon = (): void => {
    if (settled) {
      return;
    }
    settled = true;
    upstream?.destroy();
    cutOff(res);
  };

  // The API failed: a fixed 502 when nothing was sent yet, otherwise the client's response
  // is cut off. One fixed line is logged, never the URL, the method or the error itself.
  const fail = (error: unknown): void => {
    // Once the API's whole response has arrived ('end' may still be pending), an error of the
    // request to it, such as EPIPE or ECONNRESET from a body the API stopped reading, loses
    // nothing the client waits for: it is ignored, and 'end' still finishes the exchange.
    if (settled || upstreamResponse?.complete) {
      return;
    }
    settled = true;
    upstream?.destroy();
    if (res.headersSent) {
      cutOff(res);
      logger.error(`[api proxy] response from ${apiOrigin} interrupted${errorCode(error)}`, {
        timestamp: true,
      });
      return;
    }

    // Drain whatever of the request body is still arriving, so the connection stays usable,
    // and drop any header a failed relay of the API's response had already set.
    if (upstream) {
      req.unpipe(upstream);
    }
    req.resume();
    for (const name of res.getHeaderNames()) {
      res.removeHeader(name);
    }
    respond(
      res,
      502,
      {
        'Content-Type': 'text/plain; charset=utf-8',
        'Content-Length': Buffer.byteLength(unavailableText),
      },
      unavailableText,
    );
    logger.error(`[api proxy] no response from ${apiOrigin}${errorCode(error)}; answered 502`, {
      timestamp: true,
    });
  };

  res.on('finish', finish);
  res.on('close', () => {
    if (!res.writableFinished) {
      abandon();
    }
  });
  res.on('error', abandon);
  req.on('error', abandon);

  const headers = upstreamRequestHeaders(req);
  // HTTP/2 frames a body in DATA frames and has no Transfer-Encoding: a request has a body
  // unless its HEADERS frame ended the stream or it declares Content-Length 0. One of unknown
  // length goes to the API chunked (RFC 9112 6.1), since HTTP/1.1 must frame it.
  const isHttp2 = req instanceof Http2ServerRequest;
  const hasBody = isHttp2
    ? !req.stream.endAfterHeaders && req.headers['content-length'] !== '0'
    : req.headers['transfer-encoding'] !== undefined || Number(req.headers['content-length']) > 0;
  if (isHttp2 && hasBody && req.headers['content-length'] === undefined) {
    headers.push('Transfer-Encoding', 'chunked');
  }
  // A request body waits for the API's 100 Continue. The API can refuse a request before
  // reading its body and close, as Kestrel's 413 for a Content-Length over the body limit
  // does; body bytes written to that connection fail with EPIPE, and Node then destroys the
  // socket before reading the API's answer, which would turn the 413 into a 502.
  if (hasBody && req.headers.expect === undefined) {
    headers.push('Expect', '100-continue');
  }
  try {
    upstream = request({
      hostname: apiUrl.hostname,
      port: apiUrl.port,
      method: req.method,
      path: req.url,
      headers,
      agent: false,
    });
  } catch (error) {
    fail(error);
    return;
  }

  upstream.on('error', fail);
  upstream.on('response', (upstreamRes) => {
    upstreamRes.on('error', fail);
    try {
      relayResponseHead(res, upstreamRes);
    } catch (error) {
      fail(error);
      return;
    }
    upstreamResponse = upstreamRes;
    upstreamRes.pipe(res);
    upstreamRes.on('end', finish);
  });

  // The body goes on 100 Continue or after continueWaitMs, and never once the API has given
  // its final answer, which means it did not want the body (finish then drains it).
  const api = upstream;
  let bodySent = false;
  const sendBody = (): void => {
    if (!bodySent && !settled && upstreamResponse === undefined) {
      bodySent = true;
      req.pipe(api);
    }
  };
  if (hasBody) {
    api.once('continue', sendBody);
    setTimeout(sendBody, continueWaitMs);
  } else {
    sendBody();
  }
}

/** Takes `req` when it is an API request, answering it, and returns whether it did. */
type ApiRequestHandler = (req: HttpRequest, res: HttpResponse) => boolean;

/**
 * The handler that forwards `apiPrefixes` requests and leaves every other request alone.
 * It first applies the guards the dev server put in front of the built-in proxy: a URL
 * with `#` is a 400, and a Host the allowedHosts check rejects is a 403 with fixed text.
 */
function apiRequestHandler(config: ResolvedConfig, isPreview: boolean): ApiRequestHandler {
  const { allowedHosts, https } = isPreview ? config.preview : config.server;
  // Vite installs its host check only on a server without TLS, so a server.https or
  // preview.https server checks no Host here either.
  const checksHost = allowedHosts !== true && !https;
  const additionalHosts = additionalAllowedHosts(config);
  const blockedText =
    'Blocked request. This host is not allowed. To allow it, add it to ' +
    `${isPreview ? 'preview' : 'server'}.allowedHosts in vite.config.ts.`;

  return (req, res) => {
    const url = req.url ?? '';
    if (!apiPrefixes.some((prefix) => url.startsWith(prefix))) {
      return false;
    }
    if (url.includes('#')) {
      respond(res, 400, {});
      return true;
    }
    if (checksHost && !isHostAllowed(req.headers.host, allowedHosts ?? [], additionalHosts)) {
      respond(res, 403, { 'Content-Type': 'text/plain' }, blockedText);
      return true;
    }
    forwardToApi(req, res, config.logger);
    return true;
  };
}

/**
 * Puts `handleApi` in front of the 'request' listeners of a server Vite owns: Vite's connect
 * app, attached when the server was created, before the configure hooks run. An API request
 * is answered there and never reaches connect, so neither connect:dispatcher, which logs the
 * URL of every request it dispatches, nor any Vite middleware sees it. Every other request
 * goes to the listeners that were there, in order, with the server as `this` and the event's
 * own arguments.
 *
 * Every server Vite creates hands its requests to 'request' listeners, and that is all this
 * relies on, so the server is wrapped as an event emitter whatever its class: node:http's
 * server for plain HTTP; with server.https or preview.https, node:https's server when a
 * proxy is configured, otherwise (as with this configuration) node:http2's secure server,
 * which also accepts HTTP/1. Vite's HttpServer type does not name the https one. Vite
 * creates them with Node's default request classes, so each request arrives as one of the
 * HttpRequest and HttpResponse pairs, which is checked at run time; an event with other
 * arguments goes to the original listeners unchanged.
 */
function routeApiRequests(httpServer: HttpServer, handleApi: ApiRequestHandler): void {
  const server: EventEmitter = httpServer;
  // Raw listeners keep a once() wrapper, so such a listener still runs only once.
  const listeners = server.rawListeners('request');
  server.removeAllListeners('request');
  server.on('request', function routeApiRequest(this: unknown, ...args: unknown[]): void {
    const [req, res] = args;
    const isExchange =
      (req instanceof IncomingMessage && res instanceof ServerResponse) ||
      (req instanceof Http2ServerRequest && res instanceof Http2ServerResponse);
    if (isExchange && handleApi(req, res)) {
      return;
    }
    for (const listener of listeners) {
      Reflect.apply(listener, this, args);
    }
  });
}

/**
 * Proxies `/api` and `/health` to the API in place of Vite's server.proxy, whose vite:proxy
 * and vite:time debug output and "http proxy error" log carry each request URL, query
 * values included, and the upstream error's stack. Same origin, so no CORS headers.
 *
 * On every server Vite owns, for `npm run dev` and `vite preview`, over plain HTTP or with
 * server.https or preview.https, HTTP/1.1 and HTTP/2 alike, an API request is taken from the
 * server before Vite's connect app sees it (see routeApiRequests): no Vite middleware, and no
 * Vite or connect debug output (DEBUG=* included), sees an `/api` or `/health` request. In
 * middleware mode Vite owns no server (`httpServer` is null): the embedding app's server
 * dispatches its requests through Vite's connect app, so nothing is forwarded here, nothing
 * is installed and nothing is logged, and that server must route /api and /health to the
 * API itself.
 */
function apiProxy(): Plugin {
  return {
    name: 'epiinfo-api-proxy',
    configureServer(server) {
      // Middleware mode (Vitest's server among others): there is no server to take requests
      // from, and the embedding server routes /api and /health itself.
      if (server.httpServer === null) {
        return;
      }
      routeApiRequests(server.httpServer, apiRequestHandler(server.config, false));
    },
    // preview.proxy defaulted to server.proxy, so vite preview forwards the same way.
    configurePreviewServer(server) {
      routeApiRequests(server.httpServer, apiRequestHandler(server.config, true));
    },
  };
}

/**
 * The host Vite's resolveHostname binds for a server.host or preview.host value: localhost
 * when the value is unset or false, every interface (undefined) when it is true, and a
 * string as given. Vite passes that host to listen() as it is, a name included.
 */
function listenHost(host: string | boolean | undefined): string | undefined {
  if (host === undefined || host === false) {
    return 'localhost';
  }
  return host === true ? undefined : host;
}

/** Whether `address` is an IPv6 literal of the loopback address ::1, in any spelling. */
function isIpv6Loopback(address: string): boolean {
  const literal = `http://[${address}]/`;
  return isIP(address) === 6 && URL.canParse(literal) && new URL(literal).hostname === '[::1]';
}

/** Whether `address` is an IPv4 literal in the loopback block 127.0.0.0/8. */
function isIpv4Loopback(address: string): boolean {
  return isIP(address) === 4 && address.startsWith('127.');
}

/**
 * Whether `host` is a numeric loopback literal, which binds as given: 127.0.0.1, or ::1 in
 * any spelling.
 */
function isLoopbackLiteral(host: string): boolean {
  return host === '127.0.0.1' || isIpv6Loopback(host);
}

/**
 * The address a listener configured with `host` binds so that it stays on this machine, or
 * undefined when `host` is refused. Only the loopback hosts Vite itself treats as local (its
 * loopbackHosts) qualify:
 * - 127.0.0.1 and ::1, in any spelling, bind as given;
 * - localhost is resolved here, once. Every address it resolves to must be loopback, so a
 *   hosts file that maps it elsewhere cannot widen the bind, and the address to bind is the
 *   first, the one Node's listen() would pick with its default lookup.
 * Every interface (undefined), a wildcard address, any other address or name, and a
 * localhost that does not resolve are refused.
 */
async function loopbackAddress(host: string | undefined): Promise<string | undefined> {
  if (host === undefined) {
    return undefined;
  }
  if (isLoopbackLiteral(host)) {
    return host;
  }
  if (host !== 'localhost') {
    return undefined;
  }
  try {
    const addresses = await lookup(host, { all: true });
    const allLoopback =
      addresses.length > 0 &&
      addresses.every(({ address }) => isIpv4Loopback(address) || isIpv6Loopback(address));
    return allLoopback ? addresses[0].address : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The error that refuses the listener `setting`, stating `rule`. It names the setting, never
 * its value, so it can be printed as it is.
 */
function listenSettingError(setting: string, rule: string): Error {
  return new Error(`Listen setting '${setting}' is not allowed: ${rule}`);
}

/**
 * Replaces the listen() of `server` so that its socket opens on `address`, the loopback
 * address checked for its configured host `host`. Vite passes `host` to listen() as it is,
 * and Node would resolve a name such as localhost again when it listens, so a call that
 * names `host` binds `address` instead, and a call that names 127.0.0.1 or ::1 itself binds
 * that literal. Any other call (another host, no host, a callback or an options object where
 * the host goes) opens nothing and emits `error` on the server with `refusal()`, as listen()
 * does for a failure, so Vite's start rejects with it and its CLI exits 1. The replacement
 * never throws: the dev server's own listen wrapper, which Vite installs around it after
 * configureServer, is async and would lose the exception. vite preview calls it directly,
 * its retry on the next port when strictPort is off included. pinListen itself throws,
 * before any socket opens, when `server` has no listen() it can replace.
 */
function pinListen(
  server: EventEmitter,
  host: string,
  address: string,
  refusal: () => Error,
): void {
  const listen: unknown = Reflect.get(server, 'listen');
  if (typeof listen === 'function') {
    const pinnedListen = (...args: unknown[]): unknown => {
      const [port, requested, ...rest] = args;
      if (requested === host) {
        return Reflect.apply(listen, server, [port, address, ...rest]);
      }
      if (typeof requested === 'string' && isLoopbackLiteral(requested)) {
        return Reflect.apply(listen, server, args);
      }
      process.nextTick(() => server.emit('error', refusal()));
      return server;
    };
    if (Reflect.set(server, 'listen', pinnedListen)) {
      return;
    }
  }
  throw new Error(
    'Vite created a server whose listen() cannot be replaced; review devServerIsolation ' +
      'in vite.config.ts against this Vite version.',
  );
}

/**
 * Checks the HTTP listener `setting` (server.host or preview.host) of `serverName`, whose
 * configured host is `host` (listenHost), and pins `httpServer` to the address checked
 * (loopbackAddress, pinListen). Throws listenSettingError when `host` is refused, before any
 * socket opens.
 */
async function pinLoopbackListener(
  httpServer: EventEmitter,
  setting: string,
  host: string | undefined,
  serverName: string,
): Promise<void> {
  const refusal = (): Error =>
    listenSettingError(setting, `${serverName} must listen on localhost, 127.0.0.1 or ::1 only.`);
  const address = await loopbackAddress(host);
  if (host === undefined || address === undefined) {
    throw refusal();
  }
  pinListen(httpServer, host, address, refusal);
}

/**
 * Refuses the dev server `server` when it would open a separate HMR WebSocket listener on
 * anything but a numeric loopback literal. By Vite 5's rules it opens one unless server.ws is
 * false or server.hmr.server supplies the server, when it has no HTTP server of its own
 * (middleware mode, `httpServer` null) or server.hmr.port names a port other than
 * server.port, and that listener binds server.hmr.host, every interface when that is unset.
 * Vite's WebSocket server takes that host before configureServer runs and keeps its listener
 * to itself, so it cannot be pinned as pinListen pins the HTTP listener, and a name would be
 * resolved again when it listens: only 127.0.0.1 or ::1 itself is accepted. HMR that shares
 * the HTTP listener opens none, and server.hmr.host is then only the host the client
 * connects to. Throws listenSettingError, before any socket opens.
 */
function requireLoopbackHmrListener({ config, httpServer }: ViteDevServer): void {
  const { hmr, port, ws } = config.server;
  const hmrOptions = typeof hmr === 'object' ? hmr : undefined;
  const sharesPort = !hmrOptions?.port || hmrOptions.port === port;
  const separate = ws !== false && !hmrOptions?.server && (httpServer === null || !sharesPort);
  const host = hmrOptions?.host;
  if (separate && !(host && isLoopbackLiteral(host))) {
    throw listenSettingError(
      'server.hmr.host',
      'a separate HMR listener needs server.hmr.host set to 127.0.0.1 or ::1.',
    );
  }
}

// The route Vite 5 mounts launch-editor-middleware on, with no setting to turn it off: a
// request there opens a project file in the developer's editor.
const editorRoute = '/__open-in-editor';

/** Answers a request to the editor endpoint 404 with an empty body; no editor is launched. */
function refuseEditorRequest(_req: Connect.IncomingMessage, res: ServerResponse): void {
  respond(res, 404, { 'Content-Length': 0 });
}

/**
 * Replaces the handler of every middleware Vite mounted on `editorRoute` with
 * refuseEditorRequest. Each layer keeps its place and route, so every request connect
 * matched to it still reaches it: any letter case, any path below the route, and, with a
 * base other than '/', the URL Vite's base middleware has stripped of the base. Throws when
 * there is no such layer, so a Vite release that moves the endpoint fails startup instead
 * of leaving the endpoint open.
 */
function disableEditorEndpoint(middlewares: Connect.Server): void {
  const layers = middlewares.stack.filter((layer) => layer.route === editorRoute);
  if (layers.length === 0) {
    throw new Error(
      `Vite mounted no ${editorRoute} middleware to disable; review devServerIsolation ` +
        'in vite.config.ts against this Vite version.',
    );
  }
  for (const layer of layers) {
    layer.handle = refuseEditorRequest;
  }
}

/**
 * Closes `server`, whose startup is refused, and throws `error`. Vite neither closes a
 * server whose hook throws nor hands it to its caller, so what the refused server holds,
 * the dev server's file watcher among it, would otherwise stay open in the process of a
 * failed restart or of a program that called createServer or preview. The close starts
 * before this throws, and its outcome is observed, so a failed close neither replaces
 * `error` nor surfaces as an unhandled rejection. This stays synchronous because Vite calls
 * the configureServer post hook without awaiting it.
 */
function closeAndThrow(server: { close(): Promise<void> }, error: unknown): never {
  void Promise.allSettled([server.close()]);
  throw error;
}

/**
 * Keeps the development server (`npm run dev`) and `vite preview` on this machine. Vite 5
 * has development-server advisories that only a later major fixes, so:
 * - before any socket opens, startup fails unless every listener Vite will open stays on
 *   the loopback, whatever set its host, the CLI's --host included: the HTTP listener of the
 *   dev server (server.host) and of vite preview (preview.host), each then pinned to the
 *   address checked (pinLoopbackListener), and a separate HMR listener, which needs a
 *   numeric loopback server.hmr.host (requireLoopbackHmrListener). Vite's CLI prints the
 *   error and exits 1, and a restart after a config file change that fails keeps the
 *   running server;
 * - the dev server's editor endpoint answers 404 (disableEditorEndpoint).
 * The preview server has no editor endpoint and serves only the build output.
 */
function devServerIsolation(): Plugin {
  return {
    name: 'epiinfo-dev-server-isolation',
    async configureServer(server) {
      try {
        // Middleware mode leaves Vite no HTTP server of its own (`httpServer` is null): the
        // embedding server listens instead.
        if (server.httpServer !== null) {
          await pinLoopbackListener(
            server.httpServer,
            'server.host',
            listenHost(server.config.server.host),
            'the development server',
          );
        }
        requireLoopbackHmrListener(server);
      } catch (error) {
        closeAndThrow(server, error);
      }
      // Runs once Vite has installed its own middlewares, the editor endpoint among them.
      return () => {
        try {
          disableEditorEndpoint(server.middlewares);
        } catch (error) {
          closeAndThrow(server, error);
        }
      };
    },
    async configurePreviewServer(server) {
      try {
        await pinLoopbackListener(
          server.httpServer,
          'preview.host',
          listenHost(server.config.preview.host),
          'the preview server',
        );
      } catch (error) {
        closeAndThrow(server, error);
      }
    },
  };
}

// The files the development server may serve: the entry page, the application sources and
// the installed dependencies; Vite adds its own client. Resolved from this file's location,
// not the working directory, so they name this project wherever Vite is started.
const servedRoots = ['./index.html', './src', './node_modules'].map((path) =>
  fileURLToPath(new URL(path, import.meta.url)),
);

export default defineConfig({
  plugins: [devServerIsolation(), apiProxy()],

  // No public directory: the project has none and the build copies nothing from one, while
  // Vite 5 would serve its default web/public outside server.fs.allow.
  publicDir: false,

  esbuild: {
    jsx: 'automatic',
  },

  // Served by the API from Web:ClientPath (../../web/dist) at the site root, so the
  // default base '/' keeps asset URLs at /assets/*.
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    sourcemap: false,
  },

  // Development only (npm run dev); apiProxy forwards /api and /health. Same-origin
  // proxying means the API needs no CORS. The Vite 5 dev server has unpatched advisories,
  // fixed only in Vite 6.4.4 and later, outside the major this project requires, so it is
  // for a trusted local machine: devServerIsolation keeps its listeners, and those of vite
  // preview, on the loopback and disables its editor endpoint, and fs.allow, with publicDir
  // off, limits what it serves to index.html, src/ and node_modules/.
  server: {
    port: 5173,
    strictPort: true,
    fs: {
      strict: true,
      allow: servedRoots,
    },
  },

  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    // Vitest's default include also matches *.spec.ts, which would run the Playwright
    // smoke test e2e/smoke.spec.ts under Vitest; only src/**/*.test.* are unit tests.
    include: ['src/**/*.test.{ts,tsx}'],
    exclude: ['node_modules/**', 'dist/**', 'e2e/**'],
    globals: false,
    restoreMocks: true,
    clearMocks: true,
  },
});
