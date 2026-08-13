# qbPortWeaver Bridge

A Nicotine+ plugin that lets [qbPortWeaver](https://github.com/martsg666/qbPortWeaver) keep the
Soulseek listening port in sync with a VPN's forwarded port.

Nicotine+ has no remote-control interface, so there is no way for an external application to
change its listening port. This plugin adds a small JSON API on `127.0.0.1` that does exactly
that, and nothing else.

The port is applied the same way the Preferences dialog applies it - the setting is written and
the server connection is cycled - so **the new port is live in about five seconds and Nicotine+
never has to be restarted**. Transfers in progress survive; only the Soulseek connection blips.

## Installing

Easiest: in qbPortWeaver, set **Client** to **Nicotine+** under **Settings → General**, then open
the **Client** tab and click **Install plugin**. It copies the plugin into place and, if Nicotine+
is closed, offers to enable it too.

By hand: copy this folder into your Nicotine+ plugin folder, then enable **qbPortWeaver Bridge**
in **Preferences → Plugins**.

| Install | Plugin folder |
|---|---|
| Normal | `%APPDATA%\nicotine\plugins\` |
| Portable | `<folder containing Nicotine+.exe>\portable\data\plugins\` |

## How qbPortWeaver finds it

On start, the plugin writes a small file with its address and access token:

- `%LOCALAPPDATA%\qbPortWeaver\nicotine-bridge.json` - the one qbPortWeaver reads
- `<Nicotine+ data folder>\qbportweaver-bridge.json` - a copy you can find easily

qbPortWeaver picks these up on its own, so there is normally nothing to configure. If you run
Nicotine+ with a custom data folder (`-c` or `--user-data`), qbPortWeaver cannot work out where
to look - run `/qbpw-connection-file` in Nicotine+ and enter the address and token into
qbPortWeaver's settings by hand. Both are stable across restarts.

## Commands

Type these in any Nicotine+ chat window:

| Command | What it does |
|---|---|
| `/qbpw-status` | Bridge address, connection state, configured and in-use port |
| `/qbpw-port [port]` | Show the port, or change it |
| `/qbpw-test [port]` | Check whether the port is reachable from outside |
| `/qbpw-connection-file` | Where the connection details are published |
| `/qbpw-restart` | Restart the bridge without reloading the plugin |

## HTTP API

Every response is JSON. A failure carries `{"ok": false, "error": {"code": ..., "message": ...}}`;
the `code` is stable and is what qbPortWeaver branches on, the `message` is for people and may be
reworded between versions.

All paths except `GET /` require `Authorization: Bearer <token>`.

| Method | Path | What it does | Called by |
|---|---|---|---|
| `GET` | `/` | Liveness, plugin and Nicotine+ versions, and which capabilities resolved. No token required | Diagnosis |
| `GET` | `/v1/preferences` | Listening port, bound interface, UPnP state, connection state | qbPortWeaver |
| `POST` | `/v1/port` | Set the listening port, turn Nicotine+'s own UPnP off, and reconnect | qbPortWeaver |
| `GET` | `/v1/port` | Alias of `GET /v1/preferences` | Diagnosis |
| `GET` | `/v1/status` | Connection state, username, public IP, active port | qbPortWeaver |
| `POST` | `/v1/porttest` | Run a reachability check and wait up to `wait_ms` for the verdict | qbPortWeaver |
| `GET` | `/v1/porttest` | The last verdict, without starting a check | Diagnosis |
| `POST` | `/v1/reconnect` | Cycle the Soulseek connection without changing the port | Diagnosis |

The rows marked **Diagnosis** are deliberately not called by qbPortWeaver. They exist to make a
misbehaving bridge inspectable by hand with `curl`, which is the point at which the other rows are
by definition not answering. `GET /` is the one to start with: it reports the versions on both
sides and the capability map, so a Nicotine+ update that removed something the bridge depends on
can be identified directly rather than inferred from a failing endpoint.

`POST /v1/porttest` never blocks longer than the plugin's own cap, so a slow check returns
`state: "pending"` rather than timing out. Treat anything other than `state: "done"` as
undetermined, not as a closed port. Both porttest responses carry `source`: `native` when the
verdict came from Nicotine+'s own checker, `web` when it came from the fallback query, and `null`
before any check has run. The two fail differently, so it is worth knowing which one answered.

## Settings

**Preferences → Plugins → qbPortWeaver Bridge**. The defaults are fine.

| Setting | Default | Notes |
|---|---|---|
| Port | `38472` | `0` picks any free port. A busy port falls back to a free one automatically |
| Address | `127.0.0.1` | Change only if qbPortWeaver runs on another machine, which is not recommended |
| Token | generated | Cleared to reissue on next start |
| Connection file | empty | Override where the connection details are written |
| Log requests | off | Needs Nicotine+ debug logging on as well |

## If Nicotine+ was started with `--port`

That option overrides the configured port for the life of the process, and nothing can change it
- not this plugin, not the Preferences dialog. qbPortWeaver will report it and stop trying.
Remove `--port` from the shortcut and restart Nicotine+.

## Security

The API listens on loopback only and requires a bearer token.

Be aware that on Windows, loopback is not scoped per user: any process on the machine can reach
the socket, which is why the token exists. The token is protected by the containing folder's
permissions, which are the normal per-user profile permissions.

The token is not a strong secret and is not treated as one. Anything running as your user could
edit Nicotine+'s config directly and achieve the same result, so the token guards against other
local users and stray localhost clients - not against code already running as you.

## Compatibility

Nicotine+ exposes no stable plugin API for any of this, so the plugin reaches into its internals
the same way the Preferences dialog does. Every such access is probed once at startup: anything
missing is reported as unsupported on the relevant endpoint and logged, rather than crashing.
`GET /` reports the plugin and Nicotine+ versions and lists which capabilities resolved, so a
mismatch can be pinned down directly rather than inferred from a failing endpoint.

If a Nicotine+ update breaks something, the failure should be a clear message in the log and an
error in qbPortWeaver's diagnostics - not a broken Nicotine+.

## Licence

GPL-3.0-or-later - see `LICENSE`. This differs from the rest of qbPortWeaver because the plugin
is loaded into Nicotine+ and is a derivative work of it. The licence covers this folder only.
