# SPDX-License-Identifier: GPL-3.0-or-later
"""Publishes the bridge's address and token so qbPortWeaver can find it without configuration.

qbPortWeaver runs as a separate process and cannot be told the bridge's port or token any other
way, so the bridge writes both to a file at a path both sides can derive independently.

Two locations are written. The primary one sits in qbPortWeaver's own data folder, which is a
fixed path regardless of how Nicotine+ was installed. The secondary one sits in the Nicotine+
data folder, which is where a user would go looking, and which is the only one that works when
Nicotine+ and qbPortWeaver run under different Windows accounts.

The token is not a strong secret and is not treated as one: any process running as this user
could read Nicotine+'s config directly and do everything the token permits. Its job is to keep
*other* local users and stray localhost clients out, since loopback on Windows is not scoped per
user. The real protection is the containing folder's inherited per-user permissions.
"""

import json
import os
import tempfile

SCHEMA = 1
PRIMARY_FILE_NAME = "nicotine-bridge.json"
SECONDARY_FILE_NAME = "qbportweaver-bridge.json"


def primary_folder():
    """qbPortWeaver's data folder - fixed, and independent of how Nicotine+ was installed."""
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        return os.path.join(local_appdata, "qbPortWeaver")
    # Non-Windows: qbPortWeaver is Windows-only, but the plugin still has to load cleanly
    # elsewhere so that developing against a Linux Nicotine+ works.
    return os.path.join(os.path.expanduser("~"), ".local", "share", "qbPortWeaver")


def target_paths(data_folder, override=""):
    """The files to write, most authoritative first."""
    if override:
        return [override]

    paths = [os.path.join(primary_folder(), PRIMARY_FILE_NAME)]
    if data_folder:
        paths.append(os.path.join(data_folder, SECONDARY_FILE_NAME))
    return paths


def write(paths, payload, log):
    """Write ``payload`` to every path, atomically. Returns the paths actually written."""
    body = json.dumps(payload, indent=2)
    written = []

    for path in paths:
        try:
            folder = os.path.dirname(path)
            if folder:
                os.makedirs(folder, exist_ok=True)
            _atomic_write(path, body)
            written.append(path)
        except OSError as error:
            log("Could not write the connection file %s: %s", (path, error))

    return written


def _atomic_write(path, body):
    """Write via a temp file in the same folder, then rename over the target.

    Same-folder is required for the rename to be atomic, and it keeps qbPortWeaver from ever
    reading a half-written file.
    """
    folder = os.path.dirname(path) or "."
    handle, temp_path = tempfile.mkstemp(dir=folder, prefix=".qbpw-", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as file:
            file.write(body)
            file.flush()
            os.fsync(file.fileno())
        os.replace(temp_path, path)
    except BaseException:
        try:
            os.remove(temp_path)
        except OSError:
            pass
        raise


def remove(paths, own_pid, log):
    """Delete the connection files, but only the ones this process wrote.

    The pid check matters when a second Nicotine+ instance is running: its file must survive
    our shutdown, or it would be left advertising a bridge that no longer has a file.
    """
    for path in paths:
        try:
            if not os.path.exists(path):
                continue
            with open(path, "r", encoding="utf-8") as file:
                payload = json.load(file)
            if payload.get("pid") != own_pid:
                continue
            os.remove(path)
        except (OSError, ValueError) as error:
            log("Could not remove the connection file %s: %s", (path, error))
