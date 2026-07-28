# SPDX-License-Identifier: GPL-3.0-or-later
"""Exercises the bridge's HTTP surface against a fake Nicotine+.

Run: ``python plugins/testing/test_bridge.py``

Covers the behaviour that is awkward to trigger against a real client - a stalled main thread,
a port check that never answers, a ``--port`` lock, a port already taken - so those paths are
verified before qbPortWeaver ever depends on them.
"""

import json
import os
import socket
import sys
import tempfile
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.join(os.path.dirname(HERE), "qbpw_nicotine_bridge")

sys.path.insert(0, HERE)
import fake_nicotine  # noqa: E402

_TEMP = tempfile.mkdtemp(prefix="qbpw-bridge-test-")
os.environ["LOCALAPPDATA"] = os.path.join(_TEMP, "local")

EVENTS, CONFIG, CORE = fake_nicotine.install(
    data_folder=os.path.join(_TEMP, "nicotine"),
    config_file=os.path.join(_TEMP, "nicotine", "config", "config"))
os.makedirs(CONFIG.data_folder_path, exist_ok=True)

sys.path.insert(0, PLUGIN_DIR)
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location(
    "qbpw_nicotine_bridge", os.path.join(PLUGIN_DIR, "__init__.py"))
_module = importlib.util.module_from_spec(_spec)
sys.modules["qbpw_nicotine_bridge"] = _module
_spec.loader.exec_module(_module)
Plugin = _module.Plugin

FAILURES = []
PASSES = 0


def check(name, condition, detail=""):
    global PASSES
    if condition:
        PASSES += 1
        print(f"  PASS  {name}")
    else:
        FAILURES.append(f"{name}: {detail}")
        print(f"  FAIL  {name} - {detail}")


def request(plugin, method, path, body=None, token=None, headers=None):
    """Returns ``(status, payload)``; never raises for an HTTP error status."""
    url = f"http://127.0.0.1:{plugin._bridge.port}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")

    if token is not False:
        req.add_header("Authorization", f"Bearer {token or plugin._bridge.token}")
    for key, value in (headers or {}).items():
        req.add_header(key, value)

    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            return response.status, json.loads(response.read().decode())
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        try:
            return error.code, json.loads(raw)
        except ValueError:
            return error.code, {"raw": raw}


def make_plugin():
    plugin = Plugin()
    plugin.config = CONFIG
    plugin.core = CORE
    # Port 0 so concurrent runs and a busy 38472 cannot make the suite flaky.
    plugin.settings = dict(plugin.settings, http_port=0)
    plugin.loaded_notification()
    return plugin


def main():
    EVENTS.start_main_loop()
    plugin = make_plugin()
    print(f"Bridge listening on port {plugin._bridge.port}\n")

    try:
        run_tests(plugin)
    finally:
        plugin.disable()
        EVENTS.stop_main_loop()

    print(f"\n{PASSES} passed, {len(FAILURES)} failed")
    for failure in FAILURES:
        print(f"  - {failure}")
    return 1 if FAILURES else 0


def run_tests(plugin):
    print("Liveness and authentication")
    status, payload = request(plugin, "GET", "/", token=False)
    check("GET / needs no token", status == 200, f"status {status}")
    check("GET / identifies the app", payload.get("app") == "qbpw-nicotine-bridge", payload)
    check("GET / reports capabilities", payload.get("capabilities", {}).get("set_port") is True,
          payload.get("capabilities"))
    check("GET / leaks no token", "token" not in json.dumps(payload), payload)

    status, payload = request(plugin, "GET", "/v1/preferences", token=False)
    check("missing token is rejected", status == 401, f"status {status}")

    status, payload = request(plugin, "GET", "/v1/preferences", token="wrong-token")
    check("wrong token is rejected", status == 401, f"status {status}")
    check("401 carries a code", payload.get("error", {}).get("code") == "unauthorized", payload)

    status, _ = request(plugin, "GET", "/v1/preferences",
                        headers={"Origin": "https://evil.example"})
    check("browser origin is refused", status == 403, f"status {status}")

    status, _ = request(plugin, "GET", "/v1/preferences", headers={"Host": "evil.example"})
    check("non-loopback Host is refused", status == 403, f"status {status}")

    status, _ = request(plugin, "GET", "/v1/nope")
    check("unknown path is 404", status == 404, f"status {status}")

    status, _ = request(plugin, "POST", "/v1/status", body={})
    check("wrong method is 405", status == 405, f"status {status}")

    print("\nReading preferences")
    status, payload = request(plugin, "GET", "/v1/preferences")
    check("preferences succeed", status == 200 and payload["ok"], payload)
    check("configured port is read", payload["configured_port"] == 2234, payload)
    check("active port is read", payload["active_port"] == 2234, payload)
    check("bound-to-all interface is reported as empty, not null", payload["interface"] == "", payload)
    check("not locked by cli", payload["port_locked_by_cli"] is False, payload)

    print("\nSetting the port")
    writes_before = CONFIG.write_count
    status, payload = request(plugin, "POST", "/v1/port", body={"port": 51413})
    check("set port succeeds", status == 200 and payload["ok"], payload)
    check("set port reports the change", payload["changed"] is True, payload)
    check("previous port is reported", payload["previous_port"] == 2234, payload)
    check("config was written", CONFIG.write_count == writes_before + 1,
          f"{writes_before} -> {CONFIG.write_count}")
    check("port setting is a 2-tuple",
          CONFIG.sections["server"]["portrange"] == (51413, 51413),
          CONFIG.sections["server"]["portrange"])
    check("upnp was turned off", CONFIG.sections["server"]["upnp"] is False,
          CONFIG.sections["server"]["upnp"])
    check("a reconnect was triggered", payload["reconnect"] == "reconnect", payload)
    check("port mapping was torn down", CORE.portmapper.removed == 1, CORE.portmapper.removed)

    status, payload = request(plugin, "POST", "/v1/port", body={"port": 51413})
    check("setting the same port is a no-op", payload["changed"] is False, payload)
    check("no-op skips the reconnect", payload["reconnect"] == "none", payload)

    for bad, label in ((70000, "out of range"), ("x", "not a number"), (True, "a boolean")):
        status, _ = request(plugin, "POST", "/v1/port", body={"port": bad})
        check(f"port {label} is rejected", status == 400, f"status {status}")

    status, _ = request(plugin, "POST", "/v1/port", body={})
    check("missing port is rejected", status == 400, f"status {status}")

    print("\nThe bound interface")
    # Empty and absent must stay distinct: qbPortWeaver warns about an empty interface as a
    # possible leak outside the VPN, and skips the check entirely when it is absent. Folding
    # one into the other silently disables that warning.
    status, payload = request(plugin, "GET", "/v1/preferences")
    check("bound-to-all is reported as an empty string, not null",
          payload["interface"] == "", repr(payload["interface"]))

    CONFIG.sections["server"]["interface"] = "ProtonVPN"
    status, payload = request(plugin, "GET", "/v1/preferences")
    check("a named adapter is passed through", payload["interface"] == "ProtonVPN", payload["interface"])
    CONFIG.sections["server"]["interface"] = ""

    print("\nTurning UPnP off without moving the port")
    # Only the port needs a rebind; reconnecting to toggle a port mapping would drop the
    # Soulseek connection for five seconds for nothing.
    CORE.users.login_status = fake_nicotine.UserStatus.ONLINE
    current = CONFIG.sections["server"]["portrange"][0]
    CORE.users.public_port = current
    CONFIG.sections["server"]["upnp"] = True
    reconnects_before = CORE.reconnect_count

    status, payload = request(plugin, "POST", "/v1/port", body={"port": current})
    check("upnp is turned off", CONFIG.sections["server"]["upnp"] is False,
          CONFIG.sections["server"]["upnp"])
    check("no reconnect for a upnp-only change", payload["reconnect"] == "none", payload)
    check("the connection is not cycled", CORE.reconnect_count == reconnects_before,
          f"{reconnects_before} -> {CORE.reconnect_count}")
    check("a upnp-only change is not reported as a port change",
          payload["changed"] is False, payload)

    print("\nReconnecting when offline")
    CORE.users.login_status = fake_nicotine.UserStatus.OFFLINE
    connects_before = CORE.connect_count
    status, payload = request(plugin, "POST", "/v1/port", body={"port": 51414})
    check("offline uses connect, not reconnect", payload["reconnect"] == "connect", payload)
    check("connect was actually called", CORE.connect_count == connects_before + 1,
          CORE.connect_count)

    print("\nA port held by another process")
    blocker = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    blocker.bind(("0.0.0.0", 0))
    blocker.listen(1)
    taken = blocker.getsockname()[1]
    try:
        writes_before = CONFIG.write_count
        status, payload = request(plugin, "POST", "/v1/port", body={"port": taken})
        check("a taken port is refused", status == 409, f"status {status}")
        check("refusal names the cause",
              payload.get("error", {}).get("code") == "port_in_use", payload)
        check("config is untouched when refused", CONFIG.write_count == writes_before,
              f"{writes_before} -> {CONFIG.write_count}")
    finally:
        blocker.close()

    print("\nRe-applying the configured port while it is already bound")
    # Nicotine+ binds its listening socket when it connects, but only learns the port the server
    # saw once login completes - so active_port is None for a window in which the socket is
    # already in use. Re-applying the same port then must not be mistaken for someone else
    # holding it, or every sync cycle fails for as long as the client is not logged in.
    CORE.users.login_status = fake_nicotine.UserStatus.OFFLINE
    CORE.users.public_port = None

    holder = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    holder.bind(("0.0.0.0", 0))
    holder.listen(1)
    held = holder.getsockname()[1]
    try:
        CONFIG.sections["server"]["portrange"] = (held, held)
        status, payload = request(plugin, "POST", "/v1/port", body={"port": held})
        check("re-applying the configured port is not refused", status == 200,
              f"status {status} {payload.get('error', {}).get('code')}")

        # A genuinely different port that something else holds must still be refused.
        other = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        other.bind(("0.0.0.0", 0))
        other.listen(1)
        other_port = other.getsockname()[1]
        try:
            status, payload = request(plugin, "POST", "/v1/port", body={"port": other_port})
            check("a different port held by another process is still refused", status == 409,
                  f"status {status}")
        finally:
            other.close()
    finally:
        holder.close()

    CORE.users.login_status = fake_nicotine.UserStatus.ONLINE
    CORE.users.public_port = CONFIG.sections["server"]["portrange"][0]

    print("\nThe --port lock")
    CORE.cli_listen_port = 12345
    status, payload = request(plugin, "POST", "/v1/port", body={"port": 51415})
    check("a locked port is refused", status == 409, f"status {status}")
    check("refusal names the cause",
          payload.get("error", {}).get("code") == "port_locked_by_cli", payload)

    status, payload = request(plugin, "GET", "/v1/preferences")
    check("lock is advertised", payload["port_locked_by_cli"] is True, payload)
    check("effective port reflects the lock", payload["listen_port"] == 12345, payload)
    CORE.cli_listen_port = None

    print("\nConnection status")
    CORE.users.login_status = fake_nicotine.UserStatus.ONLINE
    status, payload = request(plugin, "GET", "/v1/status")
    check("status reports connected", payload["connection"] == "connected", payload)

    CORE.users.login_status = fake_nicotine.UserStatus.OFFLINE
    status, payload = request(plugin, "GET", "/v1/status")
    check("a recent port change reads as connecting", payload["connection"] == "connecting",
          payload)

    plugin._core_io._last_port_change = None
    plugin._core_io.note_disconnected(user_initiated=True)
    status, payload = request(plugin, "GET", "/v1/status")
    check("a user disconnect reads as disconnected", payload["connection"] == "disconnected",
          payload)
    CORE.users.login_status = fake_nicotine.UserStatus.ONLINE
    plugin._core_io.note_connected()

    print("\nThe port check")
    CORE.port_checker.result = True
    status, payload = request(plugin, "POST", "/v1/porttest",
                              body={"port": 51413, "wait_ms": 5000})
    check("an open port is reported open",
          payload["state"] == "done" and payload["result"] is True, payload)

    CORE.port_checker.result = False
    status, payload = request(plugin, "POST", "/v1/porttest",
                              body={"port": 40001, "wait_ms": 5000})
    check("a closed port is reported closed",
          payload["state"] == "done" and payload["result"] is False, payload)

    CORE.port_checker.never_answers = True
    status, payload = request(plugin, "POST", "/v1/porttest",
                              body={"port": 40002, "wait_ms": 800})
    check("a silent check stays pending", payload["state"] == "pending", payload)
    check("pending carries no verdict", payload["result"] is None, payload)
    CORE.port_checker.never_answers = False

    status, payload = request(plugin, "POST", "/v1/porttest",
                              body={"port": 40003, "wait_ms": 99999})
    check("the wait is capped below the client timeout", status == 200, f"status {status}")

    print("\nA stalled main thread")
    EVENTS.stalled.set()
    try:
        status, payload = request(plugin, "GET", "/v1/preferences")
        check("a stalled main thread times out cleanly", status == 504, f"status {status}")
        check("the timeout names the cause",
              payload.get("error", {}).get("code") == "main_thread_timeout", payload)
    finally:
        EVENTS.stalled.clear()

    status, payload = request(plugin, "GET", "/v1/preferences")
    check("service resumes after the stall", status == 200, f"status {status}")

    print("\nThe connection file")
    primary = os.path.join(os.environ["LOCALAPPDATA"], "qbPortWeaver", "nicotine-bridge.json")
    check("the connection file exists", os.path.exists(primary), primary)
    with open(primary, encoding="utf-8") as file:
        record = json.load(file)
    check("it carries the port", record.get("port") == plugin._bridge.port, record)
    check("it carries the token", record.get("token") == plugin._bridge.token, "token mismatch")
    check("it carries our pid", record.get("pid") == os.getpid(), record)
    check("it identifies the app", record.get("app") == "qbpw-nicotine-bridge", record)

    secondary = os.path.join(CONFIG.data_folder_path, "qbportweaver-bridge.json")
    check("the Nicotine+ copy exists", os.path.exists(secondary), secondary)

    print("\nOversized request bodies")
    status, _ = request(plugin, "POST", "/v1/port", body={"port": 51413, "pad": "x" * 20000})
    check("an oversized body is refused", status == 400, f"status {status}")

    print("\nShutdown")
    token, port = plugin._bridge.token, plugin._bridge.port
    plugin.disable()
    check("the connection file is removed", not os.path.exists(primary), primary)

    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    probe.settimeout(2)
    refused = probe.connect_ex(("127.0.0.1", port)) != 0
    probe.close()
    check("the port is released", refused, f"port {port} still accepts connections")
    check("disable is repeatable", _survives(plugin.disable), "second disable raised")
    del token


def _survives(call):
    try:
        call()
        return True
    except Exception:  # noqa: BLE001 - that is what is being tested
        return False


if __name__ == "__main__":
    sys.exit(main())
