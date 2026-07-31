# SPDX-License-Identifier: GPL-3.0-or-later
"""Turns Nicotine+'s fire-and-forget port check into something a HTTP request can wait on.

``PortChecker.check_status()`` starts a worker thread and returns immediately; the verdict
arrives later as a ``check-port-status`` event on the main thread. This module holds the
in-flight state so a request can start a check and wait for that event, and so several
overlapping requests share one check rather than each starting their own.

Releases without that native checker (the ``check-port-status`` API is not in a stable
Nicotine+ yet) fall back to querying the Soulseek port-test service over HTTP and parsing the
verdict directly - see ``_web_request`` / ``_web_port_check``. ``request()`` picks the path
from the ``port_test_native`` capability.
"""

import re
import threading
import time
import urllib.request

STATE_IDLE = "idle"
STATE_PENDING = "pending"
STATE_DONE = "done"
STATE_UNAVAILABLE = "unavailable"

# Fallback port-test service: the same one Nicotine+'s "Check Port Status" opens in a browser. Used
# only when this Nicotine+ has no scriptable checker (the native check-port-status API, upstream #3373,
# is not in a release yet). The service returns an HTML page containing "<port>/tcp open|closed".
_PORT_CHECK_URL_DEFAULT = "https://www.slsknet.org/porttest.php?port=%s"

# A verdict older than this is refreshed rather than reused. Long enough that a sync cycle
# does not re-probe the external check service every time, short enough to notice a port
# that has since closed.
STALE_AFTER = 90.0

# Nicotine+'s checker never reports failure, it just never emits - so give up eventually.
START_TIMEOUT = 20.0

# The ceiling on how long a single request may block. qbPortWeaver's HTTP client times out
# at 10 s, and a timeout there is indistinguishable from a wedged client, so always return
# a real response before then and let the caller poll.
MAX_WAIT_SECONDS = 9.0


class PortTest:
    """Shared state for the port reachability check."""

    def __init__(self, proxy, core_io, log):
        self._proxy = proxy
        self._core_io = core_io
        self._log = log
        self._lock = threading.Lock()
        self._done = threading.Event()
        self._state = STATE_IDLE
        self._port = None
        self._result = None
        self._started = 0.0

    # ------------------------------------------------- main thread (event hook)

    def on_check_port_status(self, port, is_successful):
        """Nicotine+ has a verdict. Runs on the main thread via events.connect."""
        with self._lock:
            # Nicotine+'s own port-check popover uses the same event; ignore verdicts for
            # checks we did not start.
            if self._state != STATE_PENDING or port != self._port:
                return
            self._state = STATE_DONE
            self._result = is_successful
            self._started = time.monotonic()
            self._done.set()

    # ------------------------------------------------------ main thread (start)

    def _start_on_main(self, port):
        checker = self._core_io.port_checker()
        if checker is None or not callable(getattr(checker, "check_status", None)):
            return STATE_UNAVAILABLE

        # check_status() silently does nothing while its single worker thread is still alive,
        # so starting one then would leave us waiting for an event that never comes.
        worker = getattr(checker, "_thread", None)
        if worker is not None and worker.is_alive():
            return "busy"

        checker.check_status(port)
        return "started"

    # ---------------------------------------------------------- handler threads

    def request(self, port, wait_seconds):
        """Start or join a check for ``port`` and wait up to ``wait_seconds`` for the verdict."""
        wait_seconds = max(0.0, min(float(wait_seconds), MAX_WAIT_SECONDS))

        # Prefer Nicotine+'s native checker when this version exposes it; otherwise query the Soulseek
        # port-test service directly, so reachability still works on releases without the native API.
        if self._core_io.capabilities.get("port_test_native"):
            return self._native_request(port, wait_seconds)
        return self._web_request(port, wait_seconds)

    def _native_request(self, port, wait_seconds):
        now = time.monotonic()

        with self._lock:
            if self._state == STATE_DONE and self._port == port and now - self._started < STALE_AFTER:
                return self._snapshot()

            joining = (self._state == STATE_PENDING and self._port == port
                       and now - self._started < START_TIMEOUT)
            if not joining:
                self._state = STATE_PENDING
                self._port = port
                self._result = None
                self._started = now
                self._done.clear()
                start_it = True
            else:
                start_it = False

        if start_it:
            outcome = self._proxy.call(self._start_on_main, port)
            if outcome == STATE_UNAVAILABLE:
                with self._lock:
                    self._state = STATE_UNAVAILABLE
                    return self._snapshot()
            if outcome == "busy":
                # Nicotine+'s own check is running. Report pending so the caller polls again
                # rather than treating a temporary conflict as a failure.
                with self._lock:
                    self._state = STATE_IDLE
                return {"ok": True, "state": STATE_PENDING, "port": port,
                        "result": None, "age_ms": 0}

        self._done.wait(wait_seconds)

        with self._lock:
            return self._snapshot()

    def _web_request(self, port, wait_seconds):
        """Fallback path: query the Soulseek port-test service over HTTP and parse the verdict. Runs
        on the handler thread (touches no Nicotine+ internals) and caches per port for STALE_AFTER so
        repeated checks do not hammer the service."""
        now = time.monotonic()
        with self._lock:
            if self._state == STATE_DONE and self._port == port and now - self._started < STALE_AFTER:
                return self._snapshot()

        result = _web_port_check(port, wait_seconds, self._log)

        with self._lock:
            self._port = port
            self._started = time.monotonic()
            if result is None:
                # Offline, service unreachable, or unparseable - transient, so the caller can retry.
                self._state = STATE_PENDING
            else:
                self._state = STATE_DONE
                self._result = result
            return self._snapshot()

    def peek(self):
        """Current state without starting anything."""
        with self._lock:
            return self._snapshot()

    def _snapshot(self, state=None, port=None):
        state = state or self._state
        port = port if port is not None else self._port
        age_ms = 0
        if self._started:
            age_ms = int((time.monotonic() - self._started) * 1000)
        return {
            "ok": True,
            "state": STATE_PENDING if state == STATE_IDLE else state,
            "port": port,
            "result": self._result if state == STATE_DONE else None,
            "age_ms": age_ms,
            "source": "slsknet",
        }


def _port_check_url():
    """Nicotine+'s own port-test URL when exposed, so we track it if upstream changes it."""
    try:
        import pynicotine
        url = getattr(pynicotine, "__port_checker_url__", None)
        if isinstance(url, str) and "%s" in url:
            return url
    # Fall back to the known URL if the attribute moved or is absent.
    except Exception:  # noqa: BLE001
        pass
    return _PORT_CHECK_URL_DEFAULT


def _web_port_check(port, timeout, log):
    """Query the port-test service and return True (open), False (closed), or None (undetermined)."""
    url = _port_check_url() % port
    try:
        request = urllib.request.Request(url, headers={"User-Agent": "qbPortWeaver-bridge"})
        # Fixed https host; only the integer port is interpolated, so this is not an SSRF vector.
        with urllib.request.urlopen(request, timeout=max(1.0, timeout)) as response:  # noqa: S310
            body = response.read(65536).decode("utf-8", "replace")
    # Any network or parse failure means "could not determine", never a hard error.
    except Exception as error:  # noqa: BLE001
        log("qbPortWeaver: web port check failed: %s", (error,))
        return None

    # The service echoes "<ip> Port: <port>/tcp open|closed"; match on the port we asked about.
    match = re.search(r"\b%d/tcp\s+(open|closed)\b" % port, body, re.IGNORECASE)
    if match is None:
        return None
    return match.group(1).lower() == "open"
