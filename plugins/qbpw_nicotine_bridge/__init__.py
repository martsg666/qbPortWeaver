# SPDX-License-Identifier: GPL-3.0-or-later
"""qbPortWeaver Bridge - lets qbPortWeaver manage the Soulseek listening port.

Nicotine+ has no remote-control interface, so qbPortWeaver cannot follow a VPN's forwarded
port on its own. This plugin adds a small JSON API on 127.0.0.1 that lets it read and change
the port, applying the change the same way the Preferences dialog does - by rewriting the
setting and forcing a reconnect - so the new port is live in about five seconds and Nicotine+
never has to be restarted.
"""

import os
import secrets
import threading

from pynicotine.events import events
from pynicotine.pluginsystem import BasePlugin

from bridge import errors, handshake
from bridge.core_io import INFORMATIONAL_CAPABILITIES, CoreIO
from bridge.main_thread import MainThreadProxy
from bridge.port_test import MAX_WAIT_SECONDS, STATE_DONE, PortTest
from bridge.server import API_VERSION, APP_ID, Bridge


def _read_plugin_version():
    """Read the version out of PLUGININFO, next to this file.

    PLUGININFO is the file Nicotine+ requires and the one qbPortWeaver reads to decide whether it
    ships a newer copy, so it is the version that matters. Deriving the reported version from it
    rather than repeating the number here means the two cannot drift apart. Parsing matches
    qbPortWeaver's reader: first "Version" key wins, surrounding quotes stripped.
    """
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "PLUGININFO")
    try:
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                key, separator, value = line.partition("=")
                if separator and key.strip().lower() == "version":
                    return value.strip().strip("\"'")
    # Nicotine+ will not load a plugin without this file, so this is close to unreachable - but a
    # missing version must never stop the bridge from starting.
    except OSError:
        pass
    return "unknown"


PLUGIN_VERSION = _read_plugin_version()

DEFAULT_HTTP_PORT = 38472
EVENT_PORT_STATUS = "check-port-status"
_BRIDGE_NOT_RUNNING = "The bridge is not running."


class Plugin(BasePlugin):

    def __init__(self, *args, **kwargs):

        super().__init__(*args, **kwargs)

        self.settings = {
            "http_port": DEFAULT_HTTP_PORT,
            "bind_host": "127.0.0.1",
            "token": "",
            "handshake_path": "",
            "log_requests": False
        }

        self.metasettings = {
            "http_port": {
                "description": ("Port for the local API that qbPortWeaver connects to "
                                "(0 picks a free port automatically)"),
                "type": "int",
                "minimum": 0,
                "maximum": 65535
            },
            "bind_host": {
                "description": ("Address to listen on. Leave as 127.0.0.1 unless qbPortWeaver "
                                "runs on another machine, which is not recommended"),
                "type": "string"
            },
            "token": {
                "description": ("Access token qbPortWeaver must present. Generated automatically; "
                                "clear it to issue a new one on the next start"),
                "type": "string"
            },
            "handshake_path": {
                "description": ("Override for the connection file qbPortWeaver reads. "
                                "Leave empty to use the default locations"),
                "type": "string"
            },
            "log_requests": {
                "description": "Log every API request (debug logging must also be enabled)",
                "type": "bool"
            }
        }

        self.commands = {
            "qbpw-status": {
                "description": "Show the qbPortWeaver bridge status",
                "callback": self.status_command
            },
            "qbpw-port": {
                "description": "Show or change the Soulseek listening port",
                "parameters": ["[port]"],
                "callback": self.port_command
            },
            "qbpw-test": {
                "description": "Check whether the listening port is reachable",
                "parameters": ["[port]"],
                "callback": self.test_command
            },
            "qbpw-connection-file": {
                "description": "Show where qbPortWeaver reads the bridge's connection details",
                # Chat rooms are public and users paste command output into bug reports.
                "disable": ["chatroom"],
                "callback": self.connection_file_command
            },
            "qbpw-restart": {
                "description": "Restart the qbPortWeaver bridge",
                "callback": self.restart_command
            }
        }

        self._bridge = None
        self._core_io = None
        self._port_test = None
        self._proxy = None
        self._handshake_paths = []
        self._event_connected = False

    # ---------------------------------------------------------------- lifecycle

    def loaded_notification(self):
        """Start the bridge.

        Deliberately here rather than in init(): by this point Nicotine+ has already recorded
        the plugin as enabled, so disable() is guaranteed to run even if startup fails. In
        init() that is not yet true, and a failure would strand the server thread with nothing
        left to stop it.
        """
        try:
            self._start()
        except Exception:
            self._stop()
            raise

    def disable(self):
        self._stop()

    def unloaded_notification(self):
        self._stop()

    def shutdown_notification(self):
        self._stop()

    def server_connect_notification(self):
        if self._core_io is not None:
            self._core_io.note_connected()

    def server_disconnect_notification(self, userchoice):
        if self._core_io is not None:
            self._core_io.note_disconnected(userchoice)

    # ------------------------------------------------------------- start / stop

    def _start(self):
        self._core_io = CoreIO(self.config, self.core, self.log)
        capabilities = self._core_io.probe()

        self._proxy = MainThreadProxy()
        self._port_test = PortTest(self._proxy, self._core_io, self.log)

        self._bridge = self._create_bridge(self._ensure_token())
        self._bridge.start()

        if capabilities.get("port_test_native"):
            events.connect(EVENT_PORT_STATUS, self._port_test.on_check_port_status)
            self._event_connected = True

        self._publish()
        self.log("Ready for qbPortWeaver on http://%s:%s/",
                 (self.settings["bind_host"] or "127.0.0.1", self._bridge.port))

    def _stop(self):
        if self._event_connected:
            try:
                events.disconnect(EVENT_PORT_STATUS, self._port_test.on_check_port_status)
            except (ValueError, KeyError):
                # Nicotine+ also sweeps a disabled plugin's callbacks by module name, so it may
                # already be gone.
                pass
            self._event_connected = False

        if self._handshake_paths:
            handshake.remove(self._handshake_paths, os.getpid(), self.log)
            self._handshake_paths = []

        if self._bridge is not None:
            self._bridge.stop()
            self._bridge = None
        elif self._proxy is not None:
            # Startup failed before the server existed; still release anything waiting.
            self._proxy.stopping.set()

        self._core_io = None
        self._port_test = None
        self._proxy = None

    def _create_bridge(self, token):
        """Bind the API server, falling back to any free port if the configured one is taken."""
        host = self.settings.get("bind_host") or "127.0.0.1"
        preferred = self.settings.get("http_port") or 0

        candidates = [preferred] if preferred else []
        candidates.append(0)

        last_error = None
        for candidate in candidates:
            try:
                return Bridge(
                    core_io=self._core_io,
                    port_test=self._port_test,
                    proxy=self._proxy,
                    token=token,
                    bind_host=host,
                    bind_port=candidate,
                    log=self.log,
                    log_debug=self._log_debug,
                    log_requests=bool(self.settings.get("log_requests")),
                    plugin_version=PLUGIN_VERSION,
                    nicotine_version=self._nicotine_version()
                )
            except OSError as error:
                last_error = error
                if candidate:
                    self.log("Port %s is not available (%s); picking a free port instead.",
                             (candidate, error))

        raise OSError(f"Could not open a local API port: {last_error}")

    def _ensure_token(self):
        """Return the access token, generating and saving one on first run.

        Persisted rather than regenerated per launch so the connection details a user enters by
        hand - the only option when Nicotine+ runs with a custom data folder - stay valid.
        """
        token = (self.settings.get("token") or "").strip()
        if not token:
            token = secrets.token_urlsafe(32)
            self.settings["token"] = token
        return token

    def _publish(self):
        """Write the connection file so qbPortWeaver can find the bridge without being told."""
        data_folder = getattr(self.config, "data_folder_path", "")
        self._handshake_paths = handshake.target_paths(
            data_folder, self.settings.get("handshake_path") or "")

        host = self.settings.get("bind_host") or "127.0.0.1"
        payload = {
            "schema": handshake.SCHEMA,
            "app": APP_ID,
            "api": API_VERSION,
            "plugin_version": PLUGIN_VERSION,
            "nicotine_version": self._nicotine_version(),
            "url": f"http://{host}:{self._bridge.port}/",  # NOSONAR S5332 - loopback bridge; TLS not applicable to a 127.0.0.1 IPC channel
            "host": host,
            "port": self._bridge.port,
            "token": self._bridge.token,
            "pid": os.getpid(),
            "data_folder": data_folder,
            "config_file": getattr(self.config, "config_file_path", ""),
            "portable": bool(data_folder) and "portable" in data_folder.lower()
        }

        written = handshake.write(self._handshake_paths, payload, self.log)
        if not written:
            self.log("Could not write the connection file anywhere. Enter the address and token "
                     "into qbPortWeaver by hand: see /qbpw-connection-file.")
        self._handshake_paths = written

    @staticmethod
    def _nicotine_version():
        try:
            import pynicotine
            return getattr(pynicotine, "__version__", "")
        except ImportError:
            return ""

    def _log_debug(self, message):
        try:
            from pynicotine.logfacility import log
            log.add_debug(f"qbPortWeaver Bridge: {message}")
        except ImportError:
            pass

    # ----------------------------------------------------------------- commands

    def status_command(self, _args, **_unused):
        if self._bridge is None:
            self.output(_BRIDGE_NOT_RUNNING)
            return

        core_io = self._core_io
        host = self.settings.get("bind_host") or "127.0.0.1"
        self.output(f"Bridge:      http://{host}:{self._bridge.port}/")
        self.output(f"Connection:  {core_io.connection_state()}")

        cli_port = core_io.cli_listen_port()
        if cli_port is not None:
            self.output(f"Port:        {cli_port} (fixed by --port; it cannot be changed)")
        else:
            # An ApiError here carries the sentence that names the cause and the fix, which is
            # what errors.py writes its messages for - so echo it rather than letting it escape
            # as a generic plugin failure. The remaining lines still print: a port this build
            # cannot read must not cost the user the connection state and the capability list.
            try:
                self.output(f"Port:        {core_io.configured_port()} configured, "
                            f"{core_io.active_port()} in use")
            except errors.ApiError as error:
                self.output(f"Port:        {error.message}")

        self.output(f"UPnP:        {core_io.upnp_enabled()}")

        # Same filter probe() applies to its startup warning, and for the same reason: the two
        # informational capabilities degrade nothing when absent, so naming them here reports a
        # healthy bridge as impaired. port_test_native in particular is absent on every released
        # Nicotine+ - the native check-port-status API is not in a stable build yet and PortTest
        # falls back to the web check - so without this filter the line fired for essentially
        # everyone, on the command they run to reassure themselves the bridge is fine.
        # GET / still reports the raw map: that is the diagnosis endpoint, where the question is
        # "which capabilities resolved", not "is anything wrong".
        missing = sorted(name for name, ok in core_io.capabilities.items()
                         if not ok and name not in INFORMATIONAL_CAPABILITIES)
        if missing:
            self.output(f"Unavailable: {', '.join(missing)}")

    def port_command(self, args, **_unused):
        if self._bridge is None:
            self.output(_BRIDGE_NOT_RUNNING)
            return

        text = (args or "").strip()
        if not text:
            # Echo the ApiError message for the same reason the set_port branch below reports its
            # failure: the sentence is the useful part, and a command must never escape.
            try:
                self.output(f"Port {self._core_io.configured_port()} configured, "
                            f"{self._core_io.active_port()} in use.")
            except errors.ApiError as error:
                self.output(error.message)
            return

        try:
            port = int(text)
        except ValueError:
            self.output(f"'{text}' is not a port number.")
            return

        if not 1 <= port <= 65535:
            self.output("The port must be between 1 and 65535.")
            return

        # Commands already run on the main thread, so call straight through.
        try:
            result = self._core_io.set_port(port)
        # Report the failure; a command must never break the command loop.
        except Exception as error:  # noqa: BLE001
            self.output(f"Could not set the port: {error}")
            return

        if not result["changed"]:
            self.output(f"Already listening on port {port}.")
        else:
            self.output(f"Port changed from {result['previous_port']} to {port}. "
                        "Reconnecting; it will be live in a few seconds.")

    def test_command(self, args, **_unused):
        if self._bridge is None:
            self.output(_BRIDGE_NOT_RUNNING)
            return

        text = (args or "").strip()
        try:
            port = int(text) if text else (self._core_io.active_port()
                                           or self._core_io.configured_port())
        except ValueError:
            self.output(f"'{text}' is not a port number.")
            return
        # Only the no-argument path can raise this - falling back to the configured port is what
        # needs the capability. With a port given explicitly there is nothing to read.
        except errors.ApiError as error:
            self.output(error.message)
            return

        self.output(f"Checking port {port}; the result will appear here shortly.")

        # Go through PortTest.request rather than the native checker directly, so the command
        # works on releases without the native API - the same dispatch /v1/porttest uses.
        # On a worker thread: request() blocks for up to MAX_WAIT_SECONDS, and its native path
        # marshals onto the main thread, which is where commands already run - calling it inline
        # would stall there until the proxy timed out, with the interface frozen meanwhile.
        threading.Thread(target=self._run_port_test, args=(port,),
                         name="qbpwPortTestCommand", daemon=True).start()

    def _run_port_test(self, port):
        try:
            snapshot = self._port_test.request(port, MAX_WAIT_SECONDS)
        # Report the failure; a worker thread must never die silently.
        except Exception as error:  # noqa: BLE001
            self.output(f"Could not check port {port}: {error}")
            return

        if snapshot["state"] != STATE_DONE:
            # Undetermined rather than closed: the check service was unreachable, or the verdict
            # had not arrived yet. Saying "closed" here would send the user chasing a firewall.
            self.output(f"Could not determine whether port {port} is reachable. Try again shortly.")
        elif snapshot["result"]:
            self.output(f"Port {port} is open.")
        else:
            self.output(f"Port {port} is closed. Check the forwarded port on your VPN.")

    def connection_file_command(self, _args, **_unused):
        if not self._handshake_paths:
            self.output("No connection file has been written.")
        else:
            for path in self._handshake_paths:
                self.output(path)

        if self._bridge is not None:
            # A fingerprint is enough to tell two tokens apart without disclosing either.
            self.output(f"Token starts with: {self._bridge.token[:6]}...")

    def restart_command(self, _args, **_unused):
        self._stop()
        try:
            self._start()
            self.output("The bridge has been restarted.")
        # Report the failure; a command must never break the command loop.
        except Exception as error:  # noqa: BLE001
            self.output(f"Could not restart the bridge: {error}")
