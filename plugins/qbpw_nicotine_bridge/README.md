# qbPortWeaver Bridge

A Nicotine+ plugin that lets [qbPortWeaver](https://github.com/martsg666/qbPortWeaver) keep the
Soulseek listening port in sync with a VPN's forwarded port.

Nicotine+ has no remote-control interface, so there is no way for an external application to
change its listening port. This plugin adds a small JSON API, on `127.0.0.1` by default, that does exactly
that, and nothing else.

The port is applied the same way the Preferences dialog applies it - the setting is written and
the server connection is cycled - so **the new port is live in about five seconds and Nicotine+
never has to be restarted**. Transfers in progress survive; only the Soulseek connection blips.

## Installing

Easiest: in qbPortWeaver, set **Client** to **Nicotine+** under **Settings → General**, then open
the **Nicotine+** tab and click **Install Plugin**. It copies the plugin into place and, if Nicotine+
is closed, offers to enable it too.

By hand: copy this folder into your Nicotine+ plugin folder, then in **Preferences → Plugins**
make sure plugins are turned on and enable **qbPortWeaver Bridge**.

| Install | Plugin folder |
|---|---|
| Normal | `%APPDATA%\nicotine\plugins\` |
| Portable | `<folder containing Nicotine+.exe>\portable\data\plugins\` |

## How qbPortWeaver finds it

On start, the plugin writes a small file with its address and access token:

- `%LOCALAPPDATA%\qbPortWeaver\nicotine-bridge.json` - the one qbPortWeaver reads first
- `<Nicotine+ data folder>\qbportweaver-bridge.json` - a copy you can find easily, which qbPortWeaver also reads as a fallback

qbPortWeaver picks these up on its own, so there is normally nothing to configure. If a non-portable
Nicotine+ runs under another Windows account, or the plugin's connection file setting points elsewhere, run
`/qbpw-connection-file` in a Nicotine+ private chat, open the file it lists, and copy its `url`
and `token` into qbPortWeaver's settings. Both are stable across restarts.

The file carries a `schema` number describing its layout. qbPortWeaver checks it before reading
anything else and ignores a file whose schema is newer than the version it understands, rather
than guessing at fields that may have changed meaning - so a plugin newer than the app reports as
not connected instead of misbehaving quietly. Update qbPortWeaver if you see that. A file with no
`schema` at all comes from a plugin predating the field and is read as the original layout.

## Commands

Type these in a Nicotine+ chat window. `/qbpw-connection-file` works only in a private chat, not in a
chat room, because chat rooms are public.

| Command | What it does |
|---|---|
| `/qbpw-status` | Bridge address, connection state, configured and in-use port, UPnP state, and any missing capability an endpoint depends on |
| `/qbpw-port [port]` | Show the port, or change it (UPnP is turned off too, as qbPortWeaver does) |
| `/qbpw-test [port]` | Check whether the port is reachable from outside |
| `/qbpw-connection-file` | Where the connection details are published |
| `/qbpw-restart` | Restart the bridge without reloading the plugin |

## HTTP API

Every response is JSON. A failure carries `{"ok": false, "error": {"code": ..., "message": ...}}`;
the `code` is stable and machine-readable, the `message` is for people and may be
reworded between versions.

All paths except `GET /` require `Authorization: Bearer <token>`.

| Method | Path | What it does | Called by |
|---|---|---|---|
| `GET` | `/` | Liveness, plugin and Nicotine+ versions, and which capabilities resolved. No token required | Diagnosis |
| `GET` | `/v1/preferences` | Listening port (`listen_port`, plus `configured_port`, `active_port` and `port_locked_by_cli`), bound interface, UPnP state, connection state | qbPortWeaver |
| `POST` | `/v1/port` | Set the listening port (`port`), turn Nicotine+'s own UPnP off unless `disable_upnp` is `false`, and reconnect | qbPortWeaver |
| `GET` | `/v1/port` | Alias of `GET /v1/preferences` | Diagnosis |
| `GET` | `/v1/status` | Connection state, username, public IP, active port | Diagnosis |
| `POST` | `/v1/porttest` | Run a reachability check and wait up to `wait_ms` for the verdict | qbPortWeaver |
| `GET` | `/v1/porttest` | The last verdict, without starting a check | Diagnosis |
| `POST` | `/v1/reconnect` | Cycle the Soulseek connection without changing the port | Diagnosis |

The rows marked **Diagnosis** are deliberately not called by qbPortWeaver. They exist to make a
misbehaving bridge inspectable by hand with `curl`, which is the point at which the other rows are
by definition not answering. `GET /` is the one to start with: it reports the versions on both
sides and the capability map, so a Nicotine+ update that removed something the bridge depends on
can be identified directly rather than inferred from a failing endpoint.

A successful `POST /v1/port` also carries `warnings`, a list of what it could not do on top of
saving the port: `reconnect_unsupported` and `connect_unsupported` (the new port waits for the next
connection), `reconnect_failed` (Nicotine+ raised an error while reconnecting, so it keeps
listening on the old port until the next connection), `connect_failed` (Nicotine+ was offline
and raised an error while connecting, so the port applies on the next connection), `setup_incomplete` (Nicotine+'s first-run
setup is not finished, so it is not connected), `connect_skipped` (Nicotine+ was disconnected by
the user, so it is not logged back in and the port applies when they connect), and `portmapper_unsupported` or `portmapper_failed`
(an old UPnP mapping may remain). qbPortWeaver logs each one. `POST /v1/reconnect` carries the same
list, without the two port-mapping codes and `connect_skipped`: an explicit reconnect connects.

`POST /v1/porttest` waits no longer than the plugin's own cap, so a slow check normally returns
`state: "pending"` rather than timing out. The web fallback's cap applies to each network step and
not to the name lookup, so a very slow network can still run past it; qbPortWeaver then reads the
check as undetermined. Treat anything other than `state: "done"` as
undetermined, not as a closed port. Both porttest responses carry `source`: `native` when the
verdict came from Nicotine+'s own checker, `web` when it came from the fallback query, and `null`
before any check has run. The two fail differently, so it is worth knowing which one answered.
While Nicotine+ is disconnected from the server, `POST /v1/porttest` runs no check and returns
`state: "offline"`, since nothing listens on the port until it connects.

## Settings

**Preferences → Plugins → qbPortWeaver Bridge**. The defaults are fine.

| Setting | Default | Notes |
|---|---|---|
| Port | `38472` | `0` picks any free port. A busy port falls back to a free one automatically |
| Address | `127.0.0.1` | IPv4 or IPv6 (for example `::1`). Change only if qbPortWeaver runs on another machine, which is not recommended |
| Token | generated | Cleared to reissue on next start |
| Connection file | empty | Override where the connection details are written |
| Log requests | off | Needs Nicotine+ debug logging on as well |

## If Nicotine+ was started with `--port`

That option overrides the configured port for the life of the process, and nothing can change it:
not this plugin, not the Preferences dialog. qbPortWeaver logs the reason once
and reports each sync as failed until Nicotine+ is restarted without it.
Remove `--port` from the shortcut and restart Nicotine+.

## Security

The API listens on loopback by default (see the Address setting) and requires a bearer token.

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
