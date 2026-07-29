# SPDX-License-Identifier: GPL-3.0-or-later
"""Turns Nicotine+'s fire-and-forget port check into something a HTTP request can wait on.

``PortChecker.check_status()`` starts a worker thread and returns immediately; the verdict
arrives later as a ``check-port-status`` event on the main thread. This module holds the
in-flight state so a request can start a check and wait for that event, and so several
overlapping requests share one check rather than each starting their own.
"""

import threading
import time

STATE_IDLE = "idle"
STATE_PENDING = "pending"
STATE_DONE = "done"
STATE_UNAVAILABLE = "unavailable"

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
        if not self._core_io.capabilities.get("port_test"):
            return self._snapshot(STATE_UNAVAILABLE, port)

        wait_seconds = max(0.0, min(float(wait_seconds), MAX_WAIT_SECONDS))
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
