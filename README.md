# qbPortWeaver

[![Build Check](https://github.com/martsg666/qbPortWeaver/actions/workflows/build.yml/badge.svg)](https://github.com/martsg666/qbPortWeaver/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/martsg666/qbPortWeaver)](https://github.com/martsg666/qbPortWeaver/releases/latest)
[![Chocolatey](https://img.shields.io/chocolatey/v/qbportweaver)](https://community.chocolatey.org/packages/qbportweaver)

## Overview

**qbPortWeaver** is a Windows tray application that syncs the listening port of **qBittorrent**, **Transmission**, or **Deluge** with the port assigned by your VPN provider (**ProtonVPN**, **Private Internet Access**, or any **NAT-PMP capable VPN gateway or router**).
This ensures your client always uses the VPN-provided port, improving privacy and connectivity.

The application runs in the system tray, manages configuration and logging, and automatically updates the configured client's listening port when changes are detected. It also includes a **Media Manager** for importing files into Plex-compatible library folders using TMDB title matching.

---

## Requirements

- Windows 10/11 (x64)
- ProtonVPN, Private Internet Access (PIA), or any NAT-PMP capable VPN or router with port forwarding enabled
- One of the following BitTorrent clients installed and configured:
  - **qBittorrent** with Web UI enabled
  - **Transmission** with RPC enabled (see Client Configuration below)
  - **Deluge** with Web UI plugin enabled

> **Note:** The MSI installer is not code-signed. Windows SmartScreen may show an "Unknown publisher" warning on first install - click **More info → Run anyway** to proceed. This is expected for open-source projects without a commercial code-signing certificate.

---

## Installation

### Chocolatey (recommended)

```
choco install qbportweaver
```

Run from an elevated prompt. A newly released version may briefly lag behind the GitHub release while it is in Chocolatey moderation.

### Manual (MSI)

1. Download `qbPortWeaver_<version>_Setup.msi` from the [latest release](https://github.com/martsg666/qbPortWeaver/releases/latest).
2. Run the installer (see the SmartScreen note above for the first-run warning).

After installing, open **Settings** from the tray icon to configure the application.

---

## Features

- **Automatic Port Sync**
  Detects the current VPN port and updates the BitTorrent client's listening port automatically. Supports qBittorrent (Web API), Transmission (RPC), and Deluge (Web JSON-RPC).

- **Sync on Network Change**
  In addition to the scheduled interval, qbPortWeaver can run a sync the moment a network or VPN connection change is detected, so the client follows a VPN reconnect within seconds instead of waiting for the next cycle. Rapid changes are coalesced into a single sync, and pausing still suppresses it. Enabled by default; configurable via Settings > General.

- **Multi-VPN Support**
  Supports **ProtonVPN** (via log file parsing or NAT-PMP), **Private Internet Access** (via `piactl` CLI), and any **NAT-PMP capable VPN gateway or router** (via RFC 6886 UDP port mapping). Configurable through the Settings dialog.

- **Default Port Fallback**
  When VPN is not connected, optionally sets the client's listening port to a configured default. Useful if you have a port forwarded in your router for direct connections without VPN.

- **Restart After Port Change**
  Optionally restart the BitTorrent client after updating the port to ensure changes take effect immediately.

- **Force Start**
  Optionally launch the BitTorrent client automatically if it is not running.

- **Restart on Disconnect** *(qBittorrent only)*
  Optionally restart qBittorrent when its connection status changes to disconnected. Requires the Executable and Process name to be configured.

- **Port Verification**
  After each sync, optionally checks that the listening port is actually reachable from the Internet - not just configured. Runs after a port change and periodically (every 5th cycle); a closed result is confirmed on the next cycle before a warning is logged and a tray notification is shown. Transmission and Deluge use their built-in online port checkers; qBittorrent infers reachability from incoming connections, so an idle client may report closed. Enabled by default.

- **Pause and Resume Syncing**
  A **Pause Syncing** item in the tray menu temporarily stops sync cycles (including Media Manager imports) without changing any settings. While paused, **Sync Port Now** still runs a single cycle on demand. Syncing always resumes when the application restarts.

- **VPN Interface Mismatch Warning** *(qBittorrent only)*
  Shows a tray balloon tip and logs a warning if qBittorrent's network interface does not match the configured VPN provider, or if qBittorrent is bound to all interfaces (which may cause traffic leaks).

- **Port Update Notification**
  Optionally shows a tray balloon tip when the BitTorrent client's listening port is successfully updated to a new value. Enabled by default. Configurable via Settings > General.

- **Log Alert Notifications**
  When a warning or error is logged, a one-shot tray balloon appears; clicking it opens the log viewer at the latest issue. The **Show Logs** item and the tray tooltip show a running warning/error count, which clears when you open the log viewer or clear the logs.

- **Auto-Recovery**
  After a configurable number of consecutive failed cycles (VPN disconnected, or connected but no port assigned), recovery runs through a lightweight helper service (`qbPortWeaverHelper`, LocalSystem, no UAC): for ProtonVPN/PIA it restarts the VPN service and client; for a generic NAT-PMP gateway it cycles the adapter via netsh. Recovery is also held until the failures have *persisted* long enough, so a brief blip raced through by network-change re-syncs does not force a restart. A second, independent trigger runs the same recovery when port verification confirms the port closed for a set number of checks (on by default; requires **Verify port after sync**); it fires once and re-arms only when the port tests open again. Use with care on qBittorrent, where an idle client can report closed indefinitely.

- **Post-Update Command**
  Optionally run a custom command after a successful port update (fire-and-forget). See SampleSendMail.ps1 for an example of sending an email notification with status details.

- **Media Manager**
  Imports movie and TV episode files into Plex-compatible library folders each cycle, using [TMDB](https://www.themoviedb.org) for titles and years and Plex naming (`Title (Year).ext`, `Show (Year) - SxxExx.ext`, multi-episode `SxxExxExx`) via hardlink (falling back to copy across volumes), copy, or move. Beyond the `SxxExx` pattern, episodes in season subfolders with numbered filenames (`Show/Season N/01-Title.mp4`) are recognised too (English, French, Spanish, and Italian season indicators). It can create Plex subfolders (`Movies/Title (Year)/`, `TV Shows/Show (Year)/Season XX/`) and delete emptied source folders (including `.nfo`-only ones).

  A dedicated **Media Manager** dialog (tray menu → Media Manager) configures source and library folders, previews imports (**Scan Now**), and applies or corrects them (**Import Now**), highlighting uncertain TMDB matches. A free TMDB API key is required.

- **Tray Icon Interface**
  Runs quietly in the background with a system tray icon for quick access to logs, settings, and controls.

- **Tray Status Indicator**
  After each sync cycle the tray icon shows a colored status dot: **green** (ports aligned), **orange** (VPN not connected), **red** (error), **gray** (sync paused), or **no dot** (port sync disabled). Hovering over the icon displays the current port and status, and an unviewed log count if warnings or errors have occurred (e.g. "2 Warnings, 1 Error").

- **Status Panel**
  A **Status** window (tray menu → Show Status, or double-click the tray icon) shows the live sync chain at a glance: VPN provider and connection, forwarded port, client and whether it is running, listening port (with an in-sync indicator), reachability, and the last sync time and result. Color accents flag anything out of sync, closed, or in error. It refreshes after each cycle; a **Sync Now** button runs an immediate cycle and **Test Port** checks reachability on demand.

- **Diagnostics**
  A **Run Diagnostics** action (Status panel and tray menu) runs a read-only health check across the whole sync chain and shows a pass/warning/fail checklist with a fix hint for each step: configuration, helper service, VPN connection and forwarded port, client running and reachable, ports in sync, interface binding, and outside reachability. **Re-run** refreshes it and **Copy Report** puts the results on the clipboard for a support request. It never changes the port or restarts anything.

- **Settings Dialog**
  All configuration options are editable through a dedicated Settings form (tray menu → Settings), organised into **General**, **Client**, and **Extra** tabs, with inline descriptions and tooltips for each option. A **Detect** button on the General tab finds a running or installed client (qBittorrent, Transmission, or Deluge) and fills in its selection and process details, asking you to choose when more than one is found.

- **Connection Test**
  Each client section in Settings has a **Test** button next to the URL. It checks the connection to qBittorrent, Transmission, or Deluge using the values currently entered (no need to save first), then reports success along with the current listening port, or points you to the log if it cannot connect.

- **Log Viewer**
  Built-in log viewer (tray menu → Show Logs) displays the log file with color-coded entries by level (error, warn, info, debug) and follows new entries in real time. Includes a search bar with match highlighting and prev/next navigation, dedicated prev/next buttons to step between warnings and errors without affecting the search, toggle buttons to filter by log level, a subsystem filter to isolate entries from a specific component, and a file picker to browse rotated backup files. The view is virtualized, so filters apply instantly and scrolling stays smooth even on very large logs, and the viewer keeps a low, steady memory footprint when left open for days. Adapts to the application color theme (System, Dark, or Light).

- **Consistent Theming**
  All windows - Settings, Status, Diagnostics, the log viewer, Media Manager, and the tray menu - follow your chosen color theme (System, Dark, or Light) uniformly, using the native Windows colors so text and surfaces stay legible in both light and dark mode.

- **Logging**
  Logs all operations and errors, with automatic log size management (20 MB per file, up to 5 files total). Clear logs directly from the tray menu.

- **Last-Run Status File**
  Writes a JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) after each sync cycle, exposing VPN port, client port, timestamps, and completion status for external scripts.

- **Automatic Update Checker & In-App Update**
  Checks GitHub for new releases on startup and every 12 hours, surfacing a newer version via an **Update available (X.Y.Z)** tray item and tooltip (the 12-hour check is non-intrusive; the startup form can be turned off under **Settings > General**). A **Check for Updates** item checks on demand and always reports a result. When an update is available, the update window offers **Download & Install** - it downloads the installer, runs it, and the app relaunches when the update finishes (falling back to the release page if anything goes wrong). The **About** dialog shows the current and latest version, update status, contributor links, and a **What's New** button.

- **Startup Option**
  Allows enabling or disabling automatic startup with Windows.

---

## Configuration

Settings are stored in the **Windows Registry** under `HKCU\Software\qbPortWeaver\Settings` and are editable through the built-in Settings dialog (right-click the tray icon → **Settings**).

On first run, all settings are initialized with sensible defaults.

### Available Settings

#### General

| Setting | Description | Default |
|---|---|---|
| Client | BitTorrent client to control: `qBittorrent`, `Transmission`, or `Deluge` | `qBittorrent` |
| VPN Provider | `Disabled`, `ProtonVPN`, `PIA`, or `NAT-PMP` | `Disabled` |
| NAT-PMP Adapter | Network adapter to use for NAT-PMP port mapping (only enabled when NAT-PMP is selected) | - |
| Update interval | How often to check and sync the port (seconds) | `180` |
| Sync on network change | Also run a sync immediately when a network or VPN connection change is detected, instead of waiting for the next interval (rapid changes are coalesced; pausing still suppresses it) | `True` |
| Verify port after sync | After each sync, check that the listening port is reachable from the Internet (after a port change and every 5th cycle) | `True` |
| Trigger auto-recovery when no port assigned or disconnected | Trigger auto-recovery (a VPN service restart, or adapter cycle for generic NAT-PMP gateways) after N consecutive cycles where the VPN is disconnected or assigns no forwarded port. Client-side failures do not count | `True` |
| Trigger after (consecutive failed cycles) | Number of consecutive cycles without an assigned port before auto-recovery is triggered. Recovery is also held until the failures have persisted for the time these cycles would normally span, so a brief network blip that races through several early re-syncs does not trigger it | `3` |
| Trigger auto-recovery when port stays closed | Independent trigger: runs auto-recovery when port verification confirms the port closed for the configured number of checks. Fires at most once until the port tests open again. Requires Verify port after sync | `True` |
| Trigger after (confirmed closed checks) | Number of confirmed closed checks before auto-recovery is triggered | `3` |
| Notify on port update | Show a tray balloon tip when the client's listening port is successfully updated | `True` |
| Show update form on startup | When checked, opens the update form at startup if a newer version is found. When unchecked, only a tray notification is shown (the 12-hour periodic check is always non-intrusive) | `True` |
| Post-update command | Command to run after a successful port update (leave empty to disable) | - |
| Color theme | Application color theme: `System` (follows Windows), `Dark`, or `Light`. Requires a restart to take effect | `System` |
| Debug logging | Enable verbose debug logging to the log file | `False` |

#### qBittorrent

| Setting | Description | Default |
|---|---|---|
| URL | qBittorrent Web API URL | `http://127.0.0.1:8080` |
| Username | qBittorrent Web UI username | `admin` |
| Password | qBittorrent Web UI password | - |
| Executable | Path to qBittorrent executable | `C:\Program Files\qBittorrent\qbittorrent.exe` |
| Process name | Process name used to detect if qBittorrent is running | `qbittorrent` |
| Restart after port change | Restart qBittorrent after updating the port (recommended) | `True` |
| Force start if not running | Automatically launch qBittorrent if it is not running | `True` |
| Default port (0 = disabled) | Fallback port to apply when VPN is not connected | `0` |
| Warn on interface mismatch | Warn if qBittorrent's network interface doesn't match the VPN | `True` |
| Restart on disconnect | Restart qBittorrent when its connection status changes to disconnected (requires Executable and Process name) | `True` |

#### Transmission

| Setting | Description | Default |
|---|---|---|
| URL | Transmission RPC URL | `http://127.0.0.1:9091` |
| Username | RPC username (leave empty if authentication is disabled) | - |
| Password | RPC password (leave empty if authentication is disabled) | - |
| Process name | Process name for user-space detection (e.g. `transmission-qt`) | `transmission-qt` |
| Executable | Path to Transmission executable (user-space mode) | `C:\Program Files\Transmission\transmission-qt.exe` |
| Restart after port change | Restart Transmission after updating the port (recommended) | `True` |
| Force start if not running | Automatically launch Transmission if it is not running | `True` |
| Default port (0 = disabled) | Fallback port to apply when VPN is not connected | `0` |

#### Deluge

| Setting | Description | Default |
|---|---|---|
| URL | Deluge Web UI URL | `http://127.0.0.1:8112` |
| Password | Web UI password | - |
| Executable | Path to Deluge executable | `C:\Program Files\Deluge\deluge.exe` |
| Process name | Process name used to detect if Deluge is running | `deluge` |
| Restart after port change | Restart Deluge after updating the port (recommended) | `True` |
| Force start if not running | Automatically launch Deluge if it is not running | `True` |
| Default port (0 = disabled) | Fallback port to apply when VPN is not connected | `0` |

### Media Manager Settings

Configured via tray menu → **Media Manager**.

| Setting | Description | Default |
|---|---|---|
| Enable Media Manager | Run the media importer on each sync cycle | `False` |
| TMDB API Key | API key for The Movie Database lookups (free at themoviedb.org/settings/api) | - |
| Dry Run | Preview imports without touching any files | `True` |
| Import Mode | How files are transferred to the library: `Hardlink` (default, falls back to copy for cross-volume), `Copy`, or `Move` | `Hardlink` |
| Create Folders | Organise each title into its own Plex subfolder (`Title (Year)/` for movies, `Show (Year)/Season XX/` for TV) | `True` |
| Delete Empty Folders | After importing, delete source subfolders that are empty or contain only `.nfo` files | `False` |
| Source Folders | Folders scanned for movie and TV episode files on each cycle | - |
| Movies Library | Target library folder for imported movies (leave empty to skip movie processing) | - |
| TV Shows Library | Target library folder for imported TV shows (leave empty to skip TV show processing) | - |

---

## Usage

### Startup

- The application starts minimized and runs in the system tray.
- On first run, open **Settings** from the tray menu to configure the application.

### Sync Loop

1. If VPN Provider is set to **Disabled**, the entire port sync is skipped and the cycle proceeds directly to the Media Manager step. This is useful when you only want automatic media importing without VPN port sync.
2. Checks whether the configured VPN provider is connected.
   - If **not connected** and **Default port** is 0: skips the cycle and waits for the next interval.
   - If **not connected** and **Default port** is set: uses the default port as the target and continues.
   - If **Auto-Recovery** is enabled, the failed cycle count reaches the configured threshold, and the failures have persisted long enough (so a brief blip raced through by early re-syncs is ignored): automatically triggers recovery (via the helper Windows service) - for ProtonVPN and PIA (direct or NAT-PMP mode), restarts the VPN service and client; for NAT-PMP with a generic gateway, cycles the network adapter.
3. Reads the VPN-assigned port from the configured provider (skipped if using the default port fallback). If port detection fails despite the VPN being connected, the failed cycle counter increments and auto-recovery may trigger.
4. Checks if the configured BitTorrent client is running (optionally force starts it if configured).
5. Connects to the client and retrieves the current listening port.
   - For qBittorrent: also reads the bound network interface for mismatch detection.
6. *(qBittorrent only)* If **Warn on interface mismatch** is enabled: checks that qBittorrent's network interface matches the configured VPN provider and shows a tray warning if not.
7. If ports differ:
   - Updates the client's listening port.
   - Shows a tray balloon tip if **Notify on port update** is enabled.
   - Restarts the client if configured.
8. *(qBittorrent only)* If **Restart on disconnect** is enabled (and qBittorrent was not already restarted in step 7): checks qBittorrent's connection status and restarts it if disconnected.
9. If **Verify port after sync** is enabled and the VPN is connected: checks that the port is reachable from the Internet (after a port change and every 5th cycle otherwise). A closed result is re-tested on the next cycle before a warning is raised. If **Trigger auto-recovery when port stays closed** is enabled, repeated confirmed closed checks trigger auto-recovery (a VPN service restart, or adapter cycle for NAT-PMP), at most once until the port tests open again.
10. Writes the JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) and updates the tray icon and tooltip. If the port changed this cycle, the optional post-update command is then launched (fire-and-forget) - after the status file is written, so a script that reads it (e.g. `powershell -File "C:\path\to\SampleSendMail.ps1"`) sees this cycle's result rather than the previous one.
11. Waits for the configured interval before repeating. If a manual sync was triggered, the wait is shortened to 10 seconds.
12. In parallel with the wait, if **Media Manager** is enabled: scans the configured source folders, queries TMDB for each unrecognised title, and imports files into the library with Plex-compatible names. Runs as a fire-and-forget task so a slow library scan does not delay the next port sync cycle - if a previous import is still running when the next cycle starts, the new import is skipped to avoid pile-up. In **dry-run** mode no files are touched; use **Scan Now** in the Media Manager dialog to preview results first. Uncertain TMDB matches are skipped automatically and flagged for manual review in the dialog.

### Tray Menu Options

- **Sync Port Now** - triggers an immediate sync cycle, skipping the current wait interval (works while paused, running a single cycle)
- **Pause Syncing / Resume Syncing** - temporarily stops sync cycles, including Media Manager imports; the tray icon and tooltip show the paused state. Not persisted: syncing always resumes when the application restarts
- **Show Status** - opens the Status panel showing the live sync chain (also opened by double-clicking the tray icon); includes Sync Now, Test Port, and Run Diagnostics
- **Run Diagnostics** - runs a read-only health check of the whole sync chain and shows a pass/warning/fail report with fix hints
- **Show Logs** - opens the built-in Log Viewer; shows a warning/error count badge when unviewed entries exist
- **Clear Logs** - deletes all log files and starts a fresh log
- **Settings** - opens the Settings dialog
- **Media Manager** - opens the Media Manager dialog to configure source and library folders, preview imports (Scan Now), apply them (Import Now), and clear fingerprint caches (Clear Cache)
- **Check for Updates** - checks GitHub for a newer release on demand and reports the result (also shown when already up to date)
- **About** - shows version info and update status
- **Start Automatically with Windows** - toggles the Windows startup registry entry
- **Exit** - shuts down the application

---

## Recommended Setup

### 1. BIOS Configuration

- Configure your PC BIOS to **auto-start after a power failure**, so the system recovers automatically from power outages.

### 2. Windows Auto-Logon

- Install [Sysinternals Autologon](https://learn.microsoft.com/en-us/sysinternals/downloads/autologon) to automatically log in to Windows after a reboot. This ensures your VPN client, BitTorrent client, and qbPortWeaver all start without manual intervention.

### 3. ProtonVPN Configuration

- Enable **Split Tunneling** and route only your BitTorrent client through the VPN.
- Enable **Port Forwarding** (required for qbPortWeaver to work).
- Select a **P2P server**.
- Enable **NetShield**.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set ProtonVPN to **start with Windows**.
- Set `VPN Provider` to `ProtonVPN` in qbPortWeaver Settings (reads the forwarded port from the ProtonVPN log file).

> **Alternative:** ProtonVPN also supports NAT-PMP. If you prefer not to rely on log file parsing, set `VPN Provider` to `NAT-PMP` instead and select the ProtonVPN virtual adapter in the NAT-PMP Adapter dropdown. See the NAT-PMP Configuration section below.

> **Proton's new protocols:** ProtonVPN 5.x.y adds in-house protocols (Proton WireGuard and Proton Stealth) whose tunnel adapter is named `ProTUN`. The earlier protocols name it `ProtonVPN` (standard WireGuard) or `ProtonVPN TUN` (OpenVPN). qbPortWeaver detects all of them automatically. If you switch between the earlier and the in-house protocols, reselect the active adapter wherever you have pinned it - the **NAT-PMP Adapter** dropdown and your client's **Network Interface** binding.

### 4. PIA Configuration (if using PIA instead of ProtonVPN)

- Enable **Split Tunneling** and route only your BitTorrent client through the VPN.
- Enable **Port Forwarding** in the PIA desktop client settings.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set PIA to **start with Windows**.
- Set `VPN Provider` to `PIA` in qbPortWeaver Settings.

### 5. NAT-PMP Configuration

NAT-PMP (RFC 6886) is a protocol for requesting port mappings directly from a gateway. qbPortWeaver supports it in two scenarios:

**With ProtonVPN (alternative to log file parsing):**
- ProtonVPN supports NAT-PMP natively on P2P servers. You can use this instead of the default log file approach.
- Enable **Port Forwarding** in ProtonVPN and connect to a P2P server - this enables NAT-PMP on the VPN gateway, which qbPortWeaver queries directly.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the **ProtonVPN virtual adapter** in the NAT-PMP Adapter dropdown.

> **Note:** With ProtonVPN, qbPortWeaver and the built-in port forwarding client both query the same gateway and receive the same external port - they share the same mapping rather than competing. qbPortWeaver uses that port to configure the BitTorrent client.

**With any other NAT-PMP capable VPN client or router:**
- The VPN gateway or router must support NAT-PMP (RFC 6886) with port forwarding enabled.
- Enable **port forwarding** in your VPN client or router settings.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the correct **network adapter** in the NAT-PMP Adapter dropdown - choose the virtual adapter created by your VPN client, or your LAN adapter if using a NAT-PMP capable router.

> If no adapter appears in the list, ensure the adapter is up and its gateway is responding to NAT-PMP, then click the **↻** button to refresh without reopening Settings.

### 6. Client Configuration

#### qBittorrent

- **Disable UPnP/NAT-PMP** port mapping (Options > Connection) since the port is managed externally.
  > **Note:** qBittorrent's built-in NAT-PMP tries to open ports on your local router. qbPortWeaver's NAT-PMP mode is different - it queries your VPN gateway directly using the same protocol. Disabling qBittorrent's option does not affect qbPortWeaver.
- Enable **Anonymous Mode** (Options > BitTorrent).
- Enable **Web UI** (Options > Web UI) and configure a username and password matching your qbPortWeaver Settings.
- Bind the **network interface** to your VPN adapter (Options > Advanced > Network Interface) to prevent traffic leaks outside the VPN.
  > **Note:** If you change ProtonVPN protocols, reselect the adapter here - the tunnel adapter is named `ProTUN` on the in-house protocols (Proton WireGuard, Proton Stealth), or `ProtonVPN` / `ProtonVPN TUN` on the earlier ones (standard WireGuard / OpenVPN). A stale binding triggers the interface mismatch warning.
- Set qBittorrent to **start with Windows**.

#### Transmission

- Enable **RPC** in Transmission preferences and set a username and password.
- Use the **remote session** (connect via `http://localhost:9091` in qbPortWeaver Settings). Do not use Transmission's local session; when qbPortWeaver restarts the process, the RPC endpoint is the only reliable way to communicate across restarts.
- If Transmission is installed as a **Windows service**, qbPortWeaver detects it automatically.
- If running as a **user-space process** (e.g. Transmission Qt), set the Process name (e.g. `transmission-qt`) and the Executable path so qbPortWeaver can restart it after a port change.
- **Enable your VPN client's killswitch** to prevent traffic leaks. Transmission only allows binding to an IP address (not an adapter name), and the IP assigned by the VPN typically rotates on reconnection - making bind-address rules brittle. The VPN killswitch blocks all traffic when the tunnel is down, regardless of what address Transmission is bound to. Both ProtonVPN and PIA expose this option in their desktop clients.

#### Deluge

- Enable the **Web UI plugin** (Preferences > Plugins) and set a password.
- Set the URL in qbPortWeaver Settings to match the Web UI address (default `http://127.0.0.1:8112`).
- Set the Process name (e.g. `deluge`) and Executable path so qbPortWeaver can restart it after a port change.
- Disable **UPnP** and **NAT-PMP** in Deluge preferences (Preferences > Network) since the port is managed externally.
- **Enable your VPN client's killswitch** to prevent traffic leaks. Deluge only allows binding to an IP address (not an adapter name), and the IP assigned by the VPN typically rotates on reconnection - making bind-address rules brittle. The VPN killswitch blocks all traffic when the tunnel is down, regardless of what address Deluge is bound to. Both ProtonVPN and PIA expose this option in their desktop clients.

### 7. qbPortWeaver

- Enable **Start Automatically with Windows** from the tray menu.
- On first run, open **Settings** from the tray menu, select your BitTorrent client (or click **Detect** to find it automatically), and enter the connection credentials and preferences.
- Use the **Test** button next to the client URL to confirm the connection works before saving.

---

## Logging

- All actions and errors are logged to `%LocalAppData%\qbPortWeaver\qbPortWeaver.log`.
- Log files are automatically rotated when exceeding **20 MB**, keeping up to 5 files (current + 4 backups).
- Open the **Log Viewer** from the tray menu (Show Logs). It shows color-coded entries (red for errors, gold for warnings, blue for info, orange for debug) and tails new entries live. Use the search bar to find and highlight matches with prev/next navigation, the level filter buttons to show only the levels you care about, the subsystem dropdown to isolate entries from a specific component, or the file picker to browse rotated backup files. The viewer adapts to the application color theme configured in Settings.

---

## Error Handling

The application is designed to always recover. A failing cycle never crashes the app; errors are logged and the loop retries on the next interval.

### Port Sync

- If the VPN provider is not connected and no default port is configured, the cycle is skipped and the issue is logged.
- If the VPN provider is not connected and a default port is configured, the default port is applied instead.
- If the VPN port cannot be determined, the issue is logged and the update is skipped. If Auto-Recovery is enabled, repeated failures trigger automatic recovery.
- If the BitTorrent client is not running and cannot be force started or updated, errors are logged and the loop continues after the next interval.

### Media Manager

- If a TMDB API call fails (network error, invalid key), the file is skipped and the error is logged. Other files in the same scan continue processing.
- If a folder is inaccessible (permissions, or a network share that is offline or slow to respond), it is detected quickly without stalling the cycle. A source folder is skipped with a warning while the others are still processed; if a library folder is unreachable, the library index build is skipped that cycle and retried on the next (rather than committing a partial index), resuming automatically once the share is reachable.
- If file import fails (I/O error, disk full), the individual file is skipped. The scan continues with the next file.
- If the fingerprint cache is corrupt or unreadable, it is discarded and rebuilt from scratch on the next scan.

### UI

- If the Settings, Media Manager, or About dialog encounters an error, it is displayed in the status label or logged. The main application loop is never affected.
- If the Log Viewer cannot read the log file, it degrades gracefully without crashing.

---

## Contributing

### Branch and Release Strategy

**`master`** always reflects the latest published release. Do not commit directly to `master`; it is updated only by merging a completed release branch (step 4 below).

#### Branch naming

| Purpose | Base branch | Name pattern |
|---|---|---|
| Release | Previous release branch | `2.x.y` |
| Release candidate | Release branch | `rc/<name>-<version>` |
| Hotfix | Corresponding release branch | `fix/<description>` |
| Feature | Corresponding release branch | `feature/<description>` |

Hotfix, feature, and release-candidate branches are all merged into the release branch via pull request; release-candidate branches stage a batch of changes for final testing before the release is tagged.

#### Workflow diagram

```
master  ──────────────────────────────────────────────────────────────► (always latest release)
           │                                                          ▲
           │  git checkout -b <new-release> origin/<previous-release>│ git merge --no-ff <new-release>
           ▼                                                          │
<new-release> ──┬────────────────────────── git tag v<new-release> ──┘
           │                                                  │
           ├── fix/some-bug   → PR → merge into <new-release> └─► CI/CD pipeline triggers
           └── feature/new-ui → PR → merge into <new-release>       ├─ dotnet publish (self-contained win-x64)
                                                                      ├─ WiX MSI build
                                                                      ├─ GitHub Release created
                                                                      └─ MSI + .nupkg uploaded to release
```

#### Workflow steps

1. **Create a release branch** from the previous release branch:
   ```
   git checkout -b <new-release> origin/<previous-release>
   git push -u origin <new-release>
   ```

2. **Create fix or feature branches** off the release branch and open a PR targeting it:
   ```
   git checkout -b fix/my-fix origin/<new-release>
   # or
   git checkout -b feature/my-feature origin/<new-release>
   ```
   Opening the pull request runs the **Build Check** workflow (a Release build with warnings treated as errors); make sure it passes before merging.

3. **Tag the release branch** once all testing is complete - this triggers the pipeline:
   ```
   git checkout <new-release>
   git pull --ff-only
   git tag v<new-release>
   git push origin v<new-release>
   ```
   Pushing the tag automatically triggers the **Build and Release** pipeline, which builds the app, compiles the MSI installer, creates the GitHub Release, and uploads the MSI and Chocolatey package as release assets. Once the previous Chocolatey version is approved, run the **Publish to Chocolatey** workflow manually from the Actions tab.

4. **Merge the release branch into `master`** after the pipeline completes successfully:
   ```
   git checkout master
   git merge --no-ff <new-release>
   git push origin master
   ```

5. **Do not delete release branches.** They serve as the base for future hotfixes. If a branch is accidentally deleted it can be reconstructed from its tag:
   ```
   git checkout -b <new-release> v<new-release>
   git push origin <new-release>
   ```

---

## Changelog

### v2.2.0 and later - see [GitHub Releases](https://github.com/martsg666/qbPortWeaver/releases)

### v2.0.0
- **Tray status indicator**: the tray icon now shows a colored dot (green / orange / red) reflecting the last sync result, and the tooltip shows the current port and status without opening the log file
- Settings are now stored in the **Windows Registry** (`HKCU\Software\qbPortWeaver\Settings`). Existing settings are automatically migrated from the INI file on first run
- The qBittorrent **password is now encrypted** in the registry using Windows DPAPI. Existing plaintext passwords (from INI migration or older installs) are transparently re-encrypted on first read
- New **Settings** dialog (tray menu → Settings): all options are now editable in a dedicated form with inline descriptions and tooltips, replacing the previous Notepad shortcut
- Tray balloon tip and log warning when qBittorrent's network interface doesn't match the configured VPN provider, or when bound to all interfaces (potential traffic leak). Configurable via **Warn on interface mismatch** in Settings

### v1.7.0
- **Last-run status file** (`qbPortWeaver.status.json`) written after each sync cycle to `%LocalAppData%\qbPortWeaver\`. Useful for external scripts or monitoring - exposes VPN port, client port, port change flag, timestamp, and status message
- **Clear Logs** option in the tray menu
- Improved error messages for qBittorrent Web API failures, including wrong credentials, unreachable Web UI, and HTTP errors
- Fixed a PIA issue where `piactl.exe` could hang indefinitely if it failed to return a port

### v1.6.1
- New **Default port** option: set a fallback listening port when the VPN is not connected (0 = disabled). Useful if you have a port forwarded on your router for direct connections
- Fixed PIA VPN detection failing in certain installation configurations

### v1.6.0
- Added **Private Internet Access (PIA)** VPN support via `piactl` CLI alongside ProtonVPN
- New `vpnProvider` setting to switch between ProtonVPN and PIA. Changing the provider takes effect on the next sync cycle without restarting
- New `debugMode` setting for verbose debug logging
- **Breaking change:** settings `ForceStartqBittorrent` and `PostUpdateCmd` renamed to `forceStartqBittorrent` and `postUpdateCmd`

### v1.5.0
- **Automatic update checker**: notifies on startup when a new release is available on GitHub

### v1.4.0
- New **Force start** option: automatically launches qBittorrent if it is not running during a sync cycle

### v1.3.0
- New **Post-update command** option: run a custom script or command after a successful port update (runs in the background, never blocks the sync loop)

### v1.2.1
- Fixed a crash on Windows shutdown, restart, or logoff

### v1.2.0
- Log rotation: keeps up to 3 log files (5 MB each) instead of overwriting
- Various stability improvements

### v1.1.0
- Added **Sync Port Now** tray menu option for on-demand port sync

### v1.0.0
- Initial release

---

## License

Free of use and distribution. No warranty provided.

## Author
Developed by martsg666
