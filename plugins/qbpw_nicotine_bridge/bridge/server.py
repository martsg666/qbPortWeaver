# SPDX-License-Identifier: GPL-3.0-or-later
"""The loopback HTTP server: routing, authentication, and request hygiene.

Handler threads run here. They never touch Nicotine+ state directly - every read and write
goes through ``MainThreadProxy`` into ``CoreIO``.
"""

import hmac
import json
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from bridge import errors
from bridge.main_thread import MainThreadProxy

API_VERSION = 1
APP_ID = "qbpw-nicotine-bridge"

MAX_BODY_BYTES = 8 * 1024
# Read/write deadline for one accepted connection (applied via BridgeHandler.timeout).
SOCKET_TIMEOUT_SECONDS = 10
# How long a single accept() may block. Only reached if serve_forever's selector says the socket
# is readable and the peer then vanishes, so this is a backstop rather than the polling knob.
ACCEPT_POLL_SECONDS = 1.0
# How often the accept loop re-checks whether shutdown() has been called.
SERVE_POLL_SECONDS = 0.2
# How long stop() waits for the accept loop to unwind before reporting that it did not.
SHUTDOWN_JOIN_SECONDS = 5.0

PATH_ROOT = "/"
PATH_PREFERENCES = "/v1/preferences"
PATH_PORT = "/v1/port"
PATH_STATUS = "/v1/status"
PATH_PORTTEST = "/v1/porttest"
PATH_RECONNECT = "/v1/reconnect"


class BridgeHTTPServer(ThreadingHTTPServer):
    """Threaded loopback server. ``bridge`` is attached by the plugin before serving."""

    # Handler threads must never keep the interpreter alive: disabling a plugin does not stop
    # threads it started, so without this a disable could leave Nicotine+ unable to exit.
    daemon_threads = True
    allow_reuse_address = False

    bridge = None

    def handle_error(self, request, client_address):
        # The default implementation prints a traceback to stderr, which goes nowhere useful
        # in the windowed Windows build. Failures are already reported per-request.
        pass


class BridgeHandler(BaseHTTPRequestHandler):
    """Routes one request. Every failure becomes a JSON error body, never a traceback."""

    protocol_version = "HTTP/1.1"
    server_version = "qbpwNicotineBridge/1.0"
    sys_version = ""

    # Read/write deadline for an accepted connection. Required: a timeout set on the listening
    # socket is NOT inherited by the sockets accept() returns, so without this a client that
    # connects and then stalls - or promises a Content-Length it never sends - parks a handler
    # thread forever. Threads are unbounded here, so those leak for the life of Nicotine+.
    # StreamRequestHandler.setup() applies this to the connection.
    timeout = SOCKET_TIMEOUT_SECONDS

    # -------------------------------------------------------------- entry points

    def do_GET(self):  # noqa: N802 - name mandated by BaseHTTPRequestHandler
        self._dispatch("GET")

    def do_POST(self):  # noqa: N802 - name mandated by BaseHTTPRequestHandler
        self._dispatch("POST")

    def log_message(self, format, *args):  # noqa: A002 - signature mandated by the base class
        bridge = getattr(self.server, "bridge", None)
        if bridge is not None and bridge.log_requests:
            bridge.log_debug("%s %s" % (self.address_string(), format % args))

    # ------------------------------------------------------------------ routing

    def _dispatch(self, method):
        bridge = getattr(self.server, "bridge", None)
        try:
            if bridge is None:
                raise errors.shutting_down()

            self._check_origin(bridge)
            status, payload = self._route(bridge, method)
            self._respond(status, payload)

        except errors.ApiError as error:
            self._respond(error.status, error.payload())
        except (ConnectionError, socket.timeout):
            # Client hung up mid-response; nothing useful left to say.
            pass
        except Exception as error:  # noqa: BLE001 - a handler thread must never die silently
            if bridge is not None:
                bridge.log_debug(f"Unhandled error serving {method} {self.path}: {error!r}")
            try:
                self._respond(500, {"ok": False,
                                    "error": {"code": "internal_error", "message": str(error)}})
            except (ConnectionError, socket.timeout):
                pass

    def _route(self, bridge, method):
        path = self.path.split("?", 1)[0].rstrip("/") or "/"

        if path == PATH_ROOT:
            self._require_method(method, "GET", path)
            return 200, bridge.describe()

        # Everything past the root is authenticated.
        self._authenticate(bridge)

        if path == PATH_PREFERENCES:
            self._require_method(method, "GET", path)
            return 200, bridge.preferences()

        if path == PATH_PORT:
            if method == "GET":
                return 200, bridge.preferences()
            self._require_method(method, "POST", path)
            return 200, bridge.set_port(self._json_body())

        if path == PATH_STATUS:
            self._require_method(method, "GET", path)
            return 200, bridge.status()

        if path == PATH_PORTTEST:
            if method == "GET":
                return 200, bridge.peek_port_test()
            self._require_method(method, "POST", path)
            return 200, bridge.run_port_test(self._json_body())

        if path == PATH_RECONNECT:
            self._require_method(method, "POST", path)
            return 200, bridge.reconnect()

        raise errors.not_found(path)

    @staticmethod
    def _require_method(actual, expected, path):
        if actual != expected:
            raise errors.method_not_allowed(actual, path)

    # ----------------------------------------------------------------- security

    def _check_origin(self, bridge):
        """Reject anything that looks like it came from a web page.

        A browser on this machine can reach a loopback server, so refuse requests carrying an
        Origin header. While the bridge is in its default loopback-only mode, also refuse a Host
        that is not loopback so a hostile page cannot use a rebound DNS name to get here.

        A non-loopback bind is an explicit opt-in for a qbPortWeaver instance on another machine.
        Its normal Host header names the server's LAN address, so the loopback Host restriction
        cannot apply in that mode; authenticated endpoints remain protected by the bearer token.
        """
        if self.headers.get("Origin"):
            raise errors.forbidden_origin()

        host = (self.headers.get("Host") or "").split(":", 1)[0].strip("[]")
        if _is_loopback_host(bridge.bind_host) and host and not _is_loopback_host(host):
            raise errors.forbidden_origin()

    def _authenticate(self, bridge):
        header = self.headers.get("Authorization") or ""
        scheme, _, presented = header.partition(" ")
        # Compare as bytes. hmac.compare_digest raises TypeError when a str argument holds a
        # non-ASCII character, and header values arrive latin-1 decoded - so a token with any high
        # byte would escape as a 500 "internal_error" instead of a 401, telling the caller the
        # bridge is broken when their credential is merely wrong. surrogateescape keeps the encode
        # total for any input, so nothing a client sends can make the comparison itself throw.
        # Bytes are also the form compare_digest is designed for; the constant-time property holds.
        if scheme.lower() != "bearer" or not hmac.compare_digest(
                presented.strip().encode("utf-8", "surrogateescape"),
                bridge.token.encode("utf-8")):
            raise errors.unauthorized()

    # ------------------------------------------------------------------- bodies

    def _json_body(self):
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            raise errors.bad_request("Content-Length is not a number.") from None

        if length < 0 or length > MAX_BODY_BYTES:
            raise errors.bad_request(f"Request body must be at most {MAX_BODY_BYTES} bytes.")
        if length == 0:
            return {}

        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw.decode("utf-8"))
        except ValueError as error:  # UnicodeDecodeError is a ValueError subclass, so this covers both
            raise errors.bad_request(f"Request body is not valid JSON: {error}.") from None

        if not isinstance(payload, dict):
            raise errors.bad_request("Request body must be a JSON object.")
        return payload

    def _respond(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)


class Bridge:
    """Owns the server thread and answers the endpoints.

    Kept separate from the plugin class so the Nicotine+ lifecycle contract and the bridge's
    own start/stop logic do not tangle.
    """

    def __init__(self, core_io, port_test, proxy, token, bind_host, bind_port, log, log_debug,
                 log_requests=False, plugin_version="", nicotine_version=""):
        self.core_io = core_io
        self.port_test = port_test
        self.proxy = proxy
        self.token = token
        self.log = log
        self.log_debug = log_debug
        self.log_requests = log_requests
        self.plugin_version = plugin_version
        self.nicotine_version = nicotine_version
        self.bind_host = bind_host

        self._set_port_lock = threading.Lock()

        self._server = BridgeHTTPServer((bind_host, bind_port), BridgeHandler)
        self._server.bridge = self
        # Bounds accept() itself; the per-connection deadline is BridgeHandler.timeout.
        self._server.timeout = ACCEPT_POLL_SECONDS
        self.port = self._server.server_address[1]

        self._thread = threading.Thread(target=self._serve, name="qbpwBridgeHTTP", daemon=True)

    # ---------------------------------------------------------------- lifecycle

    def start(self):
        self._thread.start()

    def _serve(self):
        try:
            self._server.serve_forever(poll_interval=SERVE_POLL_SECONDS)
        except Exception as error:  # noqa: BLE001 - the accept loop must not die silently
            self.log("The connection bridge stopped unexpectedly: %s", (error,))

    def stop(self):
        # Release any handler thread parked waiting on the main thread before tearing the
        # accept loop down, so shutdown() is not waiting on requests that can never complete.
        self.proxy.stopping.set()
        try:
            self._server.shutdown()
        except Exception:  # noqa: BLE001 - already stopping; nothing useful to do
            pass
        try:
            self._server.server_close()
        except Exception:  # noqa: BLE001 - already stopping
            pass
        if self._thread.is_alive() and self._thread is not threading.current_thread():
            self._thread.join(timeout=SHUTDOWN_JOIN_SECONDS)
            if self._thread.is_alive():
                # %g so the message reads "5 seconds", not "5.0 seconds".
                self.log("The connection bridge did not stop within %g seconds.",
                         (SHUTDOWN_JOIN_SECONDS,))

    # ---------------------------------------------------------------- endpoints

    def describe(self):
        """Unauthenticated liveness. Carries nothing a local process could not already learn."""
        return {
            "app": APP_ID,
            "api": API_VERSION,
            "ready": True,
            "plugin_version": self.plugin_version,
            "nicotine_version": self.nicotine_version,
            "auth_required": True,
            "capabilities": dict(self.core_io.capabilities),
            "port_locked_by_cli": self.core_io.cli_listen_port() is not None,
        }

    def preferences(self):
        return self.proxy.call(self.core_io.preferences, timeout=MainThreadProxy.READ_TIMEOUT)

    def status(self):
        return self.proxy.call(self.core_io.status, timeout=MainThreadProxy.READ_TIMEOUT)

    def set_port(self, body):
        port = _require_port(body)
        disable_upnp = body.get("disable_upnp", True)
        if not isinstance(disable_upnp, bool):
            raise errors.bad_request("'disable_upnp' must be true or false.")

        # Serialise so two concurrent requests cannot interleave their read-modify-write of
        # the port setting.
        with self._set_port_lock:
            return self.proxy.call(self.core_io.set_port, port, disable_upnp,
                                   timeout=MainThreadProxy.WRITE_TIMEOUT)

    def reconnect(self):
        return self.proxy.call(self.core_io.reconnect, timeout=MainThreadProxy.WRITE_TIMEOUT)

    def run_port_test(self, body):
        port = _optional_port(body)
        if port is None:
            port = self.proxy.call(self._effective_port, timeout=MainThreadProxy.READ_TIMEOUT)
        wait_ms = body.get("wait_ms", 8000)
        if not isinstance(wait_ms, int) or isinstance(wait_ms, bool) or wait_ms < 0:
            raise errors.bad_request("'wait_ms' must be a non-negative integer.")
        return self.port_test.request(port, wait_ms / 1000.0)

    def peek_port_test(self):
        return self.port_test.peek()

    def _effective_port(self):
        return self.core_io.active_port() or self.core_io.configured_port()


def _require_port(body):
    if "port" not in body:
        raise errors.bad_request("'port' is required.")
    return _validate_port(body["port"])


def _optional_port(body):
    if body.get("port") is None:
        return None
    return _validate_port(body["port"])


def _validate_port(value):
    # bool is an int subclass, and True would otherwise pass as port 1.
    if not isinstance(value, int) or isinstance(value, bool):
        raise errors.bad_request("'port' must be an integer.")
    if not 1 <= value <= 65535:
        raise errors.bad_request("'port' must be between 1 and 65535.")
    return value


def _is_loopback_host(host):
    return (host or "").strip("[]").lower() in ("127.0.0.1", "localhost", "::1")
