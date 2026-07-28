# SPDX-License-Identifier: GPL-3.0-or-later
"""Every read and write of a Nicotine+ internal, behind a one-time capability probe.

Nicotine+ exposes no plugin-facing API for the listening port, so this bridge reaches into
``config.sections["server"]`` and ``core`` exactly the way the Preferences dialog does.
Concentrating those accesses in one module means a future Nicotine+ refactor degrades to a
documented "unsupported" answer on one endpoint instead of raising from inside an HTTP
handler thread.

Everything here runs on the Nicotine+ main thread. Handler threads must go through
``MainThreadProxy``.
"""

import socket
import time

from bridge import errors

try:
    from pynicotine.slskmessages import UserStatus
    _OFFLINE = UserStatus.OFFLINE
except (ImportError, AttributeError):  # pragma: no cover - defensive against a future move
    _OFFLINE = 0

SECTION = "server"
KEY_PORTRANGE = "portrange"
KEY_UPNP = "upnp"
KEY_INTERFACE = "interface"

PORT_MIN = 1
PORT_MAX = 65535

# How long after a port change a "disconnected" reading is reported as "connecting" instead.
# Nicotine+ waits a fixed 5 s before reconnecting, then has to log in again; without this grace
# window qbPortWeaver would see our own rebind as the client having dropped offline.
RECONNECT_GRACE_SECONDS = 30.0


class CoreIO:
    """Capability-probed access to the Nicotine+ config and core singletons."""

    def __init__(self, config, core, log):
        self._config = config
        self._core = core
        self._log = log
        self._last_port_change = None
        self._manual_disconnect = False
        self._have_connected = False
        self.capabilities = {}

    # ------------------------------------------------------------------ probing

    def probe(self):
        """Probe every internal this bridge depends on. Returns the capability map."""
        server = self._server_section()

        port_range_ok = False
        if server is not None:
            value = server.get(KEY_PORTRANGE)
            port_range_ok = (
                isinstance(value, (tuple, list)) and len(value) == 2
                and all(isinstance(item, int) for item in value)
            )

        caps = {
            "read_port": port_range_ok,
            "set_port": port_range_ok and callable(getattr(self._config, "write_configuration", None)),
            "interface_name": server is not None and KEY_INTERFACE in server,
            "upnp": server is not None and KEY_UPNP in server,
            "cli_lock_detectable": hasattr(self._core, "cli_listen_port"),
            "connection_status": hasattr(self._core, "users"),
            "reconnect": callable(getattr(self._core, "reconnect", None)),
            "connect": callable(getattr(self._core, "connect", None)),
            "portmapper": callable(
                getattr(getattr(self._core, "portmapper", None), "remove_port_mapping", None)),
            "port_test": self._probe_port_test(),
        }

        missing = sorted(name for name, present in caps.items() if not present)
        if missing:
            self._log("Some Nicotine+ features are unavailable in this version: %s. "
                      "The matching API endpoints will report them as unsupported.",
                      (", ".join(missing),))

        self.capabilities = caps
        return caps

    def _probe_port_test(self):
        checker = getattr(self._core, "port_checker", None)
        if not callable(getattr(checker, "check_status", None)):
            return False
        # events.connect raises ValueError for an unknown event name, so only wire up the
        # listener when this Nicotine+ actually declares the event.
        try:
            from pynicotine.events import EVENT_NAMES
        except ImportError:
            return False
        return "check-port-status" in EVENT_NAMES

    def _server_section(self):
        sections = getattr(self._config, "sections", None)
        if not isinstance(sections, dict):
            return None
        server = sections.get(SECTION)
        return server if isinstance(server, dict) else None

    def _require(self, capability, description):
        if not self.capabilities.get(capability):
            raise errors.unsupported(description)

    # ------------------------------------------------------------------- reads

    def configured_port(self):
        self._require("read_port", "the listening port setting")
        return self._server_section()[KEY_PORTRANGE][0]

    def cli_listen_port(self):
        """The port forced by ``--port``, or None. A forced port cannot be changed at all."""
        return getattr(self._core, "cli_listen_port", None)

    def active_port(self):
        """The port Nicotine+ is actually bound to right now, or None when not logged in.

        This is the port the Soulseek server echoed back at login, so it is the only proof
        that a configured port was accepted - a bind failure leaves Nicotine+ retrying
        silently, with the configured port unchanged but nothing listening.
        """
        users = getattr(self._core, "users", None)
        return getattr(users, "public_port", None) if users is not None else None

    def interface_name(self):
        """The adapter Nicotine+ binds to.

        On Windows this is the adapter friendly name, the same string space qBittorrent
        reports, so qbPortWeaver's interface-mismatch check can consume it directly.

        Three distinct answers, and they must stay distinct: a name means bound to that
        adapter, an empty string means bound to every adapter - which is what qbPortWeaver
        warns about as a possible leak outside the VPN - and None means this Nicotine+ does
        not expose the setting at all, so no check can be made. Folding the empty string
        into None would silently turn the leak warning off.
        """
        if not self.capabilities.get("interface_name"):
            return None
        value = self._server_section().get(KEY_INTERFACE)
        return value if isinstance(value, str) else None

    def upnp_enabled(self):
        if not self.capabilities.get("upnp"):
            return None
        return bool(self._server_section().get(KEY_UPNP))

    def login_status(self):
        users = getattr(self._core, "users", None)
        return getattr(users, "login_status", None) if users is not None else None

    def port_checker(self):
        """Nicotine+'s reachability checker, or None when this version has none."""
        return getattr(self._core, "port_checker", None)

    def is_logged_in(self):
        status = self.login_status()
        return status is not None and status != _OFFLINE

    def connection_state(self):
        """One of "connected", "connecting", "disconnected".

        "connecting" covers both our own post-port-change rebind and Nicotine+'s automatic
        retry loop, so a transient offline moment is never reported as the client having
        dropped out.
        """
        if not self.capabilities.get("connection_status"):
            return None
        if self.is_logged_in():
            return "connected"
        if self._last_port_change is not None and \
                time.monotonic() - self._last_port_change < RECONNECT_GRACE_SECONDS:
            return "connecting"
        if self._have_connected and not self._manual_disconnect:
            return "connecting"
        return "disconnected"

    def preferences(self):
        cli_port = self.cli_listen_port()
        configured = self.configured_port()
        return {
            "ok": True,
            # The effective port: when --port is in force the configured value is dead weight,
            # and reporting it would make qbPortWeaver retry a change that can never take.
            "listen_port": cli_port or configured,
            "configured_port": configured,
            "active_port": self.active_port(),
            "interface": self.interface_name(),
            "upnp": self.upnp_enabled(),
            "port_locked_by_cli": cli_port is not None,
            "connection": self.connection_state(),
        }

    def status(self):
        users = getattr(self._core, "users", None)
        return {
            "ok": True,
            "connection": self.connection_state(),
            "login_status": self.login_status(),
            "username": getattr(users, "login_username", None) if users is not None else None,
            "public_ip": getattr(users, "public_ip_address", None) if users is not None else None,
            "active_port": self.active_port(),
            "configured_port": self.configured_port() if self.capabilities.get("read_port") else None,
            "port_locked_by_cli": self.cli_listen_port() is not None,
        }

    # ------------------------------------------------------------------ writes

    def set_port(self, port, disable_upnp=True):
        """Apply a new listening port and bring the connection back on it.

        Mirrors what Preferences does: write the config, then force a reconnect so the
        listening socket is rebound. Takes roughly five seconds to come back.
        """
        self._require("set_port", "the listening port setting")

        cli_port = self.cli_listen_port()
        if cli_port is not None:
            raise errors.port_locked_by_cli(cli_port)

        server = self._server_section()
        previous = server[KEY_PORTRANGE][0]
        active = self.active_port()
        upnp_on = bool(server.get(KEY_UPNP)) if self.capabilities.get("upnp") else False
        will_disable_upnp = bool(disable_upnp) and upnp_on

        # Nothing to do only if the port is both configured AND actually bound: a configured
        # port that never bound still needs the rebind below.
        if previous == port and active == port and not will_disable_upnp:
            return {"ok": True, "changed": False, "port": port, "previous_port": previous,
                    "upnp_disabled": False, "reconnect": "none", "warnings": []}

        # Check the port is free before touching the config. A bind failure inside Nicotine+
        # is silent - it just retries every five seconds forever, staying offline - so the
        # only good time to catch a stolen port is before we tear down a working connection.
        #
        # Only probe when the port is actually changing. Nicotine+ binds its listening socket
        # when it connects but only learns the port the server saw once login completes, so
        # active_port is None for a window in which the socket is already bound - and probing
        # then would find Nicotine+'s own socket and refuse to re-apply the port it is already
        # configured for. Which port a listener belongs to cannot be told apart from outside,
        # so the configured port is treated as ours.
        if port not in (active, previous) and not self._can_bind(port):
            raise errors.port_in_use(port)

        # Only the port needs a rebind. Reaching here with the port already configured and bound
        # means UPnP is the only thing changing, and Nicotine+'s own Preferences dialog tears the
        # mapping down without reconnecting - so doing so here would drop the connection for five
        # seconds for nothing, every time the client is switched to Nicotine+ with UPnP still on.
        needs_rebind = previous != port or active != port

        warnings = []
        server[KEY_PORTRANGE] = (port, port)
        if will_disable_upnp:
            server[KEY_UPNP] = False

        self._config.write_configuration()

        if will_disable_upnp:
            self._remove_port_mapping(warnings)

        action = self._reconnect(warnings) if needs_rebind else "none"
        if needs_rebind:
            self._last_port_change = time.monotonic()

        return {"ok": True, "changed": needs_rebind, "port": port, "previous_port": previous,
                "upnp_disabled": will_disable_upnp, "reconnect": action, "warnings": warnings}

    def reconnect(self):
        warnings = []
        action = self._reconnect(warnings)
        return {"ok": True, "action": action, "warnings": warnings}

    def _reconnect(self, warnings):
        """Rebind the listening socket by cycling the server connection.

        core.reconnect() only closes an existing connection, so it is a silent no-op when
        already offline - the new port would never be picked up. Offline, connect() is the
        call that binds.
        """
        if self.is_logged_in():
            if not self.capabilities.get("reconnect"):
                warnings.append("reconnect_unsupported")
                return "none"
            self._core.reconnect()
            return "reconnect"

        if not self.capabilities.get("connect"):
            warnings.append("connect_unsupported")
            return "none"

        # An incomplete setup would make connect() open the first-run wizard in the user's
        # face. The config change is already saved and will apply when they connect.
        if callable(getattr(self._config, "need_config", None)) and self._config.need_config():
            warnings.append("setup_incomplete")
            return "none"

        self._core.connect()
        return "connect"

    def _remove_port_mapping(self, warnings):
        if not self.capabilities.get("portmapper"):
            warnings.append("portmapper_unsupported")
            return
        try:
            self._core.portmapper.remove_port_mapping()
        except Exception as error:  # noqa: BLE001 - tearing down a mapping is best-effort
            self._log("Could not remove the existing port mapping: %s", (error,))
            warnings.append("portmapper_failed")

    def _can_bind(self, port):
        """Best-effort check that ``port`` is free.

        Racy by nature - something can take the port between this check and Nicotine+'s own
        bind - but it turns the common case (the previous client still holding the port) into
        an immediate, explicable error instead of a silent retry loop.
        """
        address = self.interface_address() or "0.0.0.0"
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            # Deliberately no SO_REUSEADDR: Nicotine+ does not set it on Windows either, so
            # setting it here would make this probe more permissive than the real bind.
            sock.bind((address, port))
            return True
        except OSError:
            return False
        finally:
            sock.close()

    def interface_address(self):
        """The IP Nicotine+ would bind to, when a specific interface is configured."""
        name = self.interface_name()
        if not name:
            return None
        try:
            from pynicotine.slskproto import NetworkInterfaces
            return NetworkInterfaces.get_interface_address(name)
        except Exception:  # noqa: BLE001 - purely an optimisation for the bind probe
            return None

    # ---------------------------------------------------------- event tracking

    def note_connected(self):
        self._manual_disconnect = False
        self._have_connected = True

    def note_disconnected(self, user_initiated):
        self._manual_disconnect = bool(user_initiated)

    def note_port_changed(self):
        self._last_port_change = time.monotonic()
