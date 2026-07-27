# SPDX-License-Identifier: GPL-3.0-or-later
"""Marshals work from HTTP handler threads onto the Nicotine+ main thread.

Nicotine+'s config and core objects are not thread-safe and every plugin hook normally runs
on the main thread, so handler threads must never touch them directly. ``events.invoke_main_thread``
queues a callable; the main loop drains that queue ten times a second, so the dispatch overhead
is up to ~100 ms on top of whatever the callable itself costs.
"""

import threading

from pynicotine.events import events

from bridge import errors

_UNSET = object()


class MainThreadProxy:
    """Runs a callable on the main thread and blocks the calling thread for its result."""

    # Comfortably above the ~100 ms queue drain, and above the disk I/O that
    # write_configuration() performs, while staying well inside qbPortWeaver's 10 s
    # HTTP client timeout so a wedged main thread surfaces as a clean 504.
    READ_TIMEOUT = 1.5
    WRITE_TIMEOUT = 5.0

    _WAIT_STEP = 0.05

    def __init__(self):
        self.stopping = threading.Event()

    def call(self, func, *args, timeout=READ_TIMEOUT):
        """Run ``func(*args)`` on the main thread and return its result.

        Re-raises whatever ``func`` raised, on the calling thread. Raises ``ApiError`` if the
        bridge is shutting down or the main thread did not get to the callable in time.
        """
        if self.stopping.is_set():
            raise errors.shutting_down()

        done = threading.Event()
        box = {"value": _UNSET, "error": None}

        def _run():
            # This MUST NOT raise. events.emit() attributes an exception raised here to
            # events._thread_callback, whose module is "pynicotine" rather than a plugin
            # name - so its plugin-error guard does not apply, and it calls core.quit()
            # and re-raises. An escaping exception here would terminate Nicotine+.
            try:
                box["value"] = func(*args)
            except BaseException as error:  # noqa: BLE001 - deliberately total; see above
                box["error"] = error
            finally:
                done.set()

        events.invoke_main_thread(_run)

        # Waiting in short steps rather than one long wait() so shutdown is observed promptly:
        # events._quit drains the queue once and then clears its callbacks, at which point a
        # still-queued _run will never execute and a single long wait would block this handler
        # thread for the full timeout while the application is trying to exit.
        remaining = timeout
        while remaining > 0:
            if done.wait(min(self._WAIT_STEP, remaining)):
                break
            if self.stopping.is_set():
                raise errors.shutting_down()
            remaining -= self._WAIT_STEP
        else:
            raise errors.main_thread_timeout()

        if box["error"] is not None:
            raise box["error"]

        return box["value"]
