# Settings reference

> Companion to the [README](../README.md). Every option in the Settings dialog, with its
> meaning and default.
>
> The **Setting** column reproduces the on-screen label exactly, so a row can be found by what the
> dialog shows. Three deliberate departures, and no others:
>
> - The trailing colon is dropped, as is any trailing parenthetical the Description column already
>   explains (`Dry run`, `Delete empty source folders after importing`).
> - Settings every client has (`Restart after port change`, `Force-start if not running`,
>   `Default port`) are written generically: the dialog prefixes those with the client's own name,
>   and keeping them generic lets the four client tables below line up row for row.
> - The two auto-recovery spinners both read `Trigger after` on screen, distinguished only by the
>   unit printed after the box, so that unit is appended here in parentheses to keep them apart.

Settings are stored in the **Windows Registry** under `HKCU\Software\qbPortWeaver\settings` and are editable through the built-in Settings dialog (right-click the tray icon → **Settings**).

On first run, all settings are initialized with sensible defaults.

## Available Settings

### General

| Setting | Description | Default |
|---|---|---|
| Client | Client to control: `qBittorrent`, `Transmission`, `Deluge`, or `Nicotine+` | `qBittorrent` |
| VPN provider | `Disabled`, `ProtonVPN`, `PIA`, or `NAT-PMP` | `Disabled` |
| NAT-PMP adapter | Network adapter to use for NAT-PMP port mapping (only enabled when NAT-PMP is selected) | - |
| Update interval | How often to check and sync the port (seconds) | `180` |
| Sync on network change | Also run a sync immediately when a network or VPN connection change is detected, instead of waiting for the next interval (rapid changes are coalesced; pausing still suppresses it) | `True` |
| Wait for VPN on startup | For a short grace period after the app starts, wait quietly while the VPN is still connecting instead of reporting it as disconnected, applying the default-port fallback, or triggering auto-recovery. Syncs as soon as the VPN comes up | `True` |
| Show notification when port updates | Show a tray balloon tip when the client's listening port is successfully updated | `True` |
| Show update form on startup | When checked, opens the update form at startup if a newer version is found. When unchecked, only a tray notification is shown (the 12-hour periodic check is always non-intrusive) | `True` |

### qBittorrent

| Setting | Description | Default |
|---|---|---|
| URL | qBittorrent Web API URL | `http://127.0.0.1:8080` |
| Username | qBittorrent Web UI username | `admin` |
| Password | qBittorrent Web UI password | - |
| Executable | Path to qBittorrent executable | `C:\Program Files\qBittorrent\qbittorrent.exe` |
| Process name | Process name used to detect if qBittorrent is running | `qbittorrent` |
| Restart after port change | Restart qBittorrent after updating the port (recommended) | `True` |
| Force-start if not running | Automatically launch qBittorrent if it is not running | `True` |
| Default port | Fallback port to apply when the VPN is not connected. `0` disables the fallback, so a disconnected VPN leaves the client's port alone | `0` |
| Warn when network interface doesn't match the VPN | Warn if qBittorrent's network interface doesn't match the configured VPN provider | `True` |
| Restart qBittorrent if connection status disconnects | Restart qBittorrent when its connection status changes to disconnected (requires Executable and Process name) | `True` |
| Fix the network interface binding when it goes stale | Re-apply qBittorrent's network interface when its stored identifier no longer matches the adapter it names. Also corrects a bind address the adapter no longer has, and - if the forwarded port stops answering after the adapter changed address - nudges qBittorrent into listening on the new one. Your qBittorrent settings are left as you had them | `True` |

### Transmission

| Setting | Description | Default |
|---|---|---|
| URL | Transmission RPC URL | `http://127.0.0.1:9091` |
| Username | RPC username (leave empty if authentication is disabled) | - |
| Password | RPC password (leave empty if authentication is disabled) | - |
| Process name | Process name for user-space detection (e.g. `transmission-qt`) | `transmission-qt` |
| Executable | Path to Transmission executable (user-space mode) | `C:\Program Files\Transmission\transmission-qt.exe` |
| Restart after port change | Restart Transmission after updating the port (recommended) | `True` |
| Force-start if not running | Automatically launch Transmission if it is not running | `True` |
| Default port | Fallback port to apply when the VPN is not connected. `0` disables the fallback, so a disconnected VPN leaves the client's port alone | `0` |

### Deluge

| Setting | Description | Default |
|---|---|---|
| URL | Deluge Web UI URL | `http://127.0.0.1:8112` |
| Password | Web UI password | - |
| Executable | Path to Deluge executable | `C:\Program Files\Deluge\deluge.exe` |
| Process name | Process name used to detect if Deluge is running | `deluge` |
| Restart after port change | Restart Deluge after updating the port (recommended) | `True` |
| Force-start if not running | Automatically launch Deluge if it is not running | `True` |
| Default port | Fallback port to apply when the VPN is not connected. `0` disables the fallback, so a disconnected VPN leaves the client's port alone | `0` |

### Nicotine+

Nicotine+ is a Soulseek client, not a BitTorrent one, and it has no remote-control interface at all.
qbPortWeaver drives it through a small plugin it installs for you (see the setup section below).
There is no "Restart after port change" setting: the plugin applies the port to the running client,
so the change is live within a few seconds and a restart would only discard settings.

| Setting | Description | Default |
|---|---|---|
| Plugin URL | Address of the bridge plugin inside Nicotine+. Found automatically; use ⟳ to fill it in | `http://127.0.0.1:38472` |
| Plugin token | Access token the plugin issues. Found automatically; use ⟳ to fill it in | - |
| Executable | Path to the Nicotine+ executable, also used to find a portable installation's data folder | `C:\Program Files\Nicotine+\Nicotine+.exe` |
| Process name | Process name used to detect if Nicotine+ is running | `Nicotine+` |
| Force-start if not running | Automatically launch Nicotine+ if it is not running | `True` |
| Warn when network interface doesn't match the VPN | Warn if Nicotine+'s network interface doesn't match the configured VPN provider | `True` |
| Default port | Fallback port to apply when the VPN is not connected. `0` disables the fallback, so a disconnected VPN leaves the client's port alone | `0` |

### Auto-Recovery

Auto-recovery restarts your VPN service (or cycles the adapter for a generic NAT-PMP gateway) when the VPN stops providing a working forwarded port. The **Test** button runs the recovery action on demand.

Two limits keep it from acting where it cannot help, since every recovery briefly takes the tunnel down and interrupts transfers:

- **Conditions a restart cannot fix are excluded.** If the provider reports that port forwarding is switched off in its own settings, or that the connected server region does not offer it, that is logged as a warning and no recovery is triggered, however long the condition lasts.
- **Consecutive attempts are capped.** After 3 recoveries in a row that fail to produce a forwarded port, auto-recovery is suspended, and says so in the log. It resumes automatically as soon as a port is read successfully. A remedy that has not worked three times running is not addressing the cause, so repeating it on a timer would only keep interrupting the connection. The **Test** button is never affected by the cap.

There is also a cheaper remedy it tries first. If the port stays closed and the adapter qBittorrent is bound to changed address, qBittorrent is nudged into listening on the new address before the VPN is restarted, since a restart cannot move a listener that is stuck on the old one. If that does not reopen the port, the next round restarts the VPN as before. This needs **Fix the network interface binding when it goes stale** on as well as the two settings in the table below, since it changes a client setting to force the rebind.

| Setting | Description | Default |
|---|---|---|
| Check that the forwarded port is open after each sync | After each sync, check that the listening port is reachable from the Internet (after a port change and every 5th cycle) | `True` |
| Trigger auto-recovery when port stays closed | Independent trigger: runs auto-recovery when port verification confirms the port closed for the configured number of checks. Fires at most once until a scheduled check reports the port open again. Requires the port check above | `True` |
| Trigger after (confirmed closed checks) | Number of confirmed closed checks before auto-recovery is triggered | `3` |
| Trigger auto-recovery when no port assigned or disconnected | Trigger auto-recovery (a VPN service restart, or adapter cycle for generic NAT-PMP gateways) after N consecutive cycles where the VPN is disconnected or assigns no forwarded port. Client-side failures do not count | `True` |
| Trigger after (consecutive failed cycles) | Number of consecutive cycles without an assigned port before auto-recovery is triggered. Recovery is also held until the failures have persisted for the time these cycles would normally span, so a brief network blip that races through several early re-syncs does not trigger it | `3` |

### Extra

| Setting | Description | Default |
|---|---|---|
| Color theme | Application color theme: `System` (follows Windows), `Dark`, or `Light`. Requires a restart to take effect | `System` |
| Post-update | Command to run after a successful port update (leave empty to disable) | - |
| Enable debug logging | Enable verbose debug logging to the log file | `False` |

## Media Manager Settings

Configured via tray menu → **Media Manager**.

| Setting | Description | Default |
|---|---|---|
| Enable Media Manager | Run the media importer on each sync cycle | `False` |
| TMDB API Key | API key for The Movie Database lookups (free at themoviedb.org/settings/api) | - |
| Dry run | Preview imports without touching any files | `True` |
| Import mode | How files are transferred to the library: `Hardlink` (default, falls back to copy for cross-volume), `Copy`, or `Move` | `Hardlink` |
| Create Plex folder structure when importing | Organise each title into its own Plex subfolder (`Title (Year)/` for movies, `Show (Year)/Season XX/` for TV) | `True` |
| Delete empty source folders after importing | After importing, delete source subfolders that are empty or contain only `.nfo` files | `False` |
| Source Folders | Folders scanned for movie and TV episode files on each cycle | - |
| Movies library | Target library folder for imported movies (leave empty to skip movie processing) | - |
| TV shows library | Target library folder for imported TV shows (leave empty to skip TV show processing) | - |

