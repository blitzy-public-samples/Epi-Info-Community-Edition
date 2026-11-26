// Vite build, development server and Vitest configuration for the web client.
//
// No JSX plugin: Vite's built-in esbuild compiles TSX with the automatic JSX runtime,
// matching tsconfig.json "jsx": "react-jsx", so @vitejs/plugin-react is not needed.
import type { EventEmitter } from 'node:events';
import type { ClientRequest, OutgoingHttpHeaders } from 'node:http';
import { IncomingMessage, ServerResponse, request } from 'node:http';
import { Http2ServerRequest, Http2ServerResponse, constants as http2Constants } from 'node:http2';
import { isIP } from 'node:net';
import type { HttpServer, Logger, Plugin, ResolvedConfig } from 'vite';
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

export default defineConfig({
  plugins: [apiProxy()],

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
  // proxying means the API needs no CORS.
  server: {
    port: 5173,
    strictPort: true,
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
