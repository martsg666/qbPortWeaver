# SPDX-License-Identifier: GPL-3.0-or-later
"""Runs the real bridge against a fake Nicotine+, so qbPortWeaver can be developed without one.

    python plugins/testing/bridge_harness.py [--port 38472] [--token abc] [--scenario name]

Scenarios reproduce the states a real client will not produce to order:

    normal        everything works (default)
    locked        started with --port, so the port cannot be changed
    offline       not logged in to Soulseek
    porttest-none the reachability check never answers
    porttest-off  this Nicotine+ has no reachability check at all
    stalled       the main thread is wedged, so every call times out

The connection file is written exactly as the plugin writes it, so qbPortWeaver's discovery
path is exercised too rather than being bypassed with hand-entered settings.
"""

import argparse
import os
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.join(os.path.dirname(HERE), "qbpw_nicotine_bridge")
sys.path.insert(0, HERE)

import fake_nicotine  # noqa: E402

SCENARIOS = ("normal", "locked", "offline", "porttest-none", "porttest-off", "stalled")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=38472,
                        help="port to listen on (0 picks a free one)")
    parser.add_argument("--token", default="harness-token",
                        help="bearer token qbPortWeaver must present")
    parser.add_argument("--scenario", choices=SCENARIOS, default="normal")
    parser.add_argument("--data-folder", default="",
                        help="fake Nicotine+ data folder (defaults to a temp folder)")
    args = parser.parse_args()

    temp = args.data_folder or tempfile.mkdtemp(prefix="qbpw-harness-")
    os.makedirs(temp, exist_ok=True)
    os.environ.setdefault("LOCALAPPDATA", os.path.join(temp, "local"))

    events, config, core = fake_nicotine.install(
        data_folder=temp, config_file=os.path.join(temp, "config", "config"))

    plugin_class = _load_plugin()

    events.start_main_loop()
    plugin = plugin_class()
    plugin.config = config
    plugin.core = core
    plugin.settings = dict(plugin.settings, http_port=args.port, token=args.token)

    _apply_scenario(args.scenario, events, core)

    plugin.loaded_notification()

    print(f"qbPortWeaver bridge harness - scenario '{args.scenario}'")
    print(f"  URL        http://127.0.0.1:{plugin._bridge.port}/")
    print(f"  Token      {plugin._bridge.token}")
    print(f"  Data       {temp}")
    for path in plugin._handshake_paths:
        print(f"  Published  {path}")
    print("\nPress Ctrl+C to stop.")

    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\nStopping.")
    finally:
        plugin.disable()
        events.stop_main_loop()

    return 0


def _load_plugin():
    import importlib.util

    sys.path.insert(0, PLUGIN_DIR)
    spec = importlib.util.spec_from_file_location(
        "qbpw_nicotine_bridge", os.path.join(PLUGIN_DIR, "__init__.py"))
    module = importlib.util.module_from_spec(spec)
    sys.modules["qbpw_nicotine_bridge"] = module
    spec.loader.exec_module(module)
    return module.Plugin


def _apply_scenario(scenario, events, core):
    if scenario == "locked":
        core.cli_listen_port = 12345
    elif scenario == "offline":
        core.users.login_status = fake_nicotine.UserStatus.OFFLINE
        core.users.public_port = None
    elif scenario == "porttest-none":
        core.port_checker.never_answers = True
    elif scenario == "porttest-off":
        core.port_checker = None
    elif scenario == "stalled":
        events.stalled.set()


if __name__ == "__main__":
    sys.exit(main())
