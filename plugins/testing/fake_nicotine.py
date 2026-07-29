# SPDX-License-Identifier: GPL-3.0-or-later
"""A stand-in for the parts of Nicotine+ the bridge touches.

Installing this into ``sys.modules`` lets the real plugin code run without Nicotine+, which
makes the awkward parts testable on demand: a main-thread queue that can be stalled, a port
checker whose verdict can be withheld, a ``--port`` lock, and a config that records writes.

It also backs the harness qbPortWeaver's own client is developed against, so both sides of the
protocol are exercised before either meets the real thing.
"""

import sys
import threading
import time
import types

EVENT_NAMES = {
    "check-port-status",
    "quit",
    "server-login",
    "server-disconnect",
    "server-reconnect",
    "start",
    "thread-callback",
}


class UserStatus:
    OFFLINE = 0
    AWAY = 1
    ONLINE = 2


class FakeEvents:
    """Mimics the real event bus, including its 10 Hz main-thread drain."""

    def __init__(self):
        self._callbacks = {}
        self._queue = []
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self.stalled = threading.Event()
        self._thread = None

    def start_main_loop(self):
        self._thread = threading.Thread(target=self._run, name="FakeMainThread", daemon=True)
        self._thread.start()

    def stop_main_loop(self):
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2)

    def _run(self):
        while not self._stop.is_set():
            # `stalled` simulates a main thread busy elsewhere (rescanning shares, say), which
            # is what the bridge's main-thread timeout exists to survive.
            if not self.stalled.is_set():
                with self._lock:
                    pending, self._queue = self._queue, []
                for callback, args, kwargs in pending:
                    callback(*args, **kwargs)
            time.sleep(0.1)

    def connect(self, event_name, function):
        if event_name not in EVENT_NAMES:
            raise ValueError(f"Unknown event {event_name}")
        self._callbacks.setdefault(event_name, []).append(function)

    def disconnect(self, event_name, function):
        self._callbacks.get(event_name, []).remove(function)

    def emit(self, event_name, *args, **kwargs):
        for callback in list(self._callbacks.get(event_name, [])):
            callback(*args, **kwargs)

    def emit_main_thread(self, event_name, *args, **kwargs):
        with self._lock:
            self._queue.append((lambda *a, **k: self.emit(event_name, *a, **k), args, kwargs))

    def invoke_main_thread(self, callback, *args, **kwargs):
        with self._lock:
            self._queue.append((callback, args, kwargs))


class FakeConfig:

    def __init__(self, data_folder, config_file):
        self.data_folder_path = data_folder
        self.config_file_path = config_file
        self.config_loaded = True
        self.write_count = 0
        self.sections = {
            "server": {
                "portrange": (2234, 2234),
                "upnp": True,
                "interface": "",
                "server": ("server.slsknet.org", 2242),
            },
            "plugins": {"enable": True, "enabled": []},
        }

    def write_configuration(self):
        self.write_count += 1

    def need_config(self):
        return False


class FakePortChecker:
    """Nicotine+'s checker: starts a thread, answers later, ignores overlapping requests."""

    def __init__(self, events):
        self._events = events
        self._thread = None
        self.result = True
        self.delay = 0.2
        self.never_answers = False

    def check_status(self, port):
        if self._thread is not None and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._check, args=(port,), daemon=True)
        self._thread.start()

    def _check(self, port):
        time.sleep(self.delay)
        if self.never_answers:
            return
        self._events.emit_main_thread("check-port-status", port, self.result)


class FakeUsers:

    def __init__(self):
        self.login_status = UserStatus.ONLINE
        self.login_username = "tester"
        self.public_ip_address = "203.0.113.7"
        self.public_port = 2234


class FakePortMapper:

    def __init__(self):
        self.removed = 0

    def remove_port_mapping(self):
        self.removed += 1


class FakeCore:

    def __init__(self, events):
        self.users = FakeUsers()
        self.port_checker = FakePortChecker(events)
        self.portmapper = FakePortMapper()
        self.cli_listen_port = None
        self.reconnect_count = 0
        self.connect_count = 0
        self._config = None

    def bind_config(self, config):
        self._config = config

    def reconnect(self):
        self.reconnect_count += 1
        self._apply_port()

    def connect(self):
        self.connect_count += 1
        self.users.login_status = UserStatus.ONLINE
        self._apply_port()

    def _apply_port(self):
        if self._config is not None:
            self.users.public_port = self._config.sections["server"]["portrange"][0]


class BasePlugin:
    """The subset of Nicotine+'s BasePlugin the bridge relies on."""

    commands = {}
    settings = {}
    metasettings = {}
    parent = None
    config = None
    core = None
    human_name = "qbPortWeaver Bridge"

    def __init__(self, *args, **kwargs):
        del args, kwargs

    def init(self):
        pass

    def loaded_notification(self):
        pass

    def disable(self):
        pass

    def unloaded_notification(self):
        pass

    def shutdown_notification(self):
        pass

    def server_connect_notification(self):
        pass

    def server_disconnect_notification(self, userchoice):
        pass

    def log(self, msg, msg_args=None):
        print(f"[plugin] {msg % msg_args if msg_args else msg}")

    def output(self, text):
        print(f"[command] {text}")


def install(data_folder, config_file):
    """Register the fake modules and return ``(events, config, core)``."""
    events = FakeEvents()
    config = FakeConfig(data_folder, config_file)
    core = FakeCore(events)
    core.bind_config(config)

    pynicotine = types.ModuleType("pynicotine")
    pynicotine.__version__ = "3.4.0-fake"
    pynicotine.__application_name__ = "Nicotine+"

    events_module = types.ModuleType("pynicotine.events")
    events_module.events = events
    events_module.EVENT_NAMES = EVENT_NAMES

    pluginsystem = types.ModuleType("pynicotine.pluginsystem")
    pluginsystem.BasePlugin = BasePlugin

    slskmessages = types.ModuleType("pynicotine.slskmessages")
    slskmessages.UserStatus = UserStatus

    logfacility = types.ModuleType("pynicotine.logfacility")
    logfacility.log = types.SimpleNamespace(add_debug=lambda message: None)

    config_module = types.ModuleType("pynicotine.config")
    config_module.config = config

    core_module = types.ModuleType("pynicotine.core")
    core_module.core = core

    for name, module in (
        ("pynicotine", pynicotine),
        ("pynicotine.events", events_module),
        ("pynicotine.pluginsystem", pluginsystem),
        ("pynicotine.slskmessages", slskmessages),
        ("pynicotine.logfacility", logfacility),
        ("pynicotine.config", config_module),
        ("pynicotine.core", core_module),
    ):
        sys.modules[name] = module

    return events, config, core
