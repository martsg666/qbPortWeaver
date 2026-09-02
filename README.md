# qbPortWeaver

[![Build Check](https://github.com/martsg666/qbPortWeaver/actions/workflows/build.yml/badge.svg)](https://github.com/martsg666/qbPortWeaver/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/martsg666/qbPortWeaver)](https://github.com/martsg666/qbPortWeaver/releases/latest)
[![Chocolatey](https://img.shields.io/chocolatey/v/qbportweaver)](https://community.chocolatey.org/packages/qbportweaver)

## Overview

**qbPortWeaver** is a Windows tray application that syncs the listening port of **qBittorrent**, **Transmission**, **Deluge**, or **Nicotine+** with the port assigned by your VPN provider (**ProtonVPN**, **PIA**, or any **NAT-PMP capable VPN gateway or router**).
This ensures your client always uses the VPN-provided port, improving privacy and connectivity.

The application runs in the system tray, manages configuration and logging, and automatically updates the configured client's listening port when changes are detected. It also includes a **Media Manager** for importing files into Plex-compatible library folders using TMDB title matching.

---

## Requirements

- Windows 10/11 (x64)
- ProtonVPN, Private Internet Access (PIA), or any NAT-PMP capable VPN or router with port forwarding enabled
- One of the following clients installed and configured:
  - **qBittorrent** with Web UI enabled
  - **Transmission** with RPC enabled (see Client Configuration below)
  - **Deluge** with Web UI plugin enabled
  - **Nicotine+** (Soulseek) with the bridge plugin, which qbPortWeaver installs for you

> **Note:** The MSI installer is not code-signed. Windows SmartScreen may show an "Unknown publisher" warning on first install - click **More info → Run anyway** to proceed. This is expected for open-source projects without a commercial code-signing certificate.

---

## Installation

All options install the same application. New versions appear on the GitHub release page first; the package managers follow once the new version clears their moderation.

### Manual (MSI)

1. Download `qbPortWeaver_<version>_Setup.msi` from the [latest release](https://github.com/martsg666/qbPortWeaver/releases/latest).
2. Run the installer (see the SmartScreen note above for the first-run warning).

### winget

```
winget install --id martsg666.qbPortWeaver -e
```

Built into Windows 10/11, no extra tooling needed.

### Chocolatey

```
choco install qbportweaver
```

Run from an elevated prompt.

After installing, open **Settings** from the tray icon to configure the application.

---

## Features

- **Automatic Port Sync**
  Detects the current VPN port and updates the client's listening port automatically. Supports qBittorrent (Web API), Transmission (RPC), Deluge (Web JSON-RPC), and Nicotine+ (via a bridge plugin qbPortWeaver installs for you). Nicotine+ applies the change to the running client, so its port follows the VPN without a restart.

- **Sync on Network Change**
  In addition to the scheduled interval, qbPortWeaver can run a sync the moment a network or VPN connection change is detected, so the client follows a VPN reconnect within seconds instead of waiting for the next cycle. Rapid changes are coalesced into a single sync, and pausing still suppresses it. Enabled by default; configurable via Settings → General.

- **Multi-VPN Support**
  Supports **ProtonVPN** (via log file parsing or NAT-PMP), **PIA** (via `piactl` CLI), and any **NAT-PMP capable VPN gateway or router** (via RFC 6886 port mapping, requested for both TCP and UDP). Configurable through the Settings dialog.

- **Default Port Fallback**
  When VPN is not connected, optionally sets the client's listening port to a configured default. Useful if you have a port forwarded in your router for direct connections without VPN.

- **Wait for VPN at Startup**
  For a short grace period after the app starts, a VPN that is still connecting is treated as expected rather than a failure: the tray stays neutral, no warning is logged, and the default-port fallback and auto-recovery are held. The port syncs as soon as the VPN comes up. Enabled by default; configurable via Settings → General.

- **Restart After Port Change**
  Optionally restart the client after updating the port to ensure changes take effect immediately. Nicotine+ needs no restart - its plugin applies the port to the running client - so it has no such setting.

- **Force-Start**
  Optionally launch the client automatically if it is not running.

- **Restart on Disconnect** *(qBittorrent only)*
  Optionally restart qBittorrent when its connection status changes to disconnected. Requires the Executable and Process name to be configured. Restarts stop after three consecutive attempts that do not clear the disconnect, since a cause the client keeps in its own configuration cannot be fixed by restarting; they resume automatically once it reconnects.

- **Port Verification**
  After each sync, optionally checks that the listening port is actually reachable from the Internet - not just configured. Runs after a port change and periodically (every 5th cycle); a closed result is confirmed on the next cycle before a warning is logged and a tray notification is shown. Transmission and Deluge use their projects' online port checkers; qBittorrent infers reachability from incoming connections, so an idle client may report closed. Nicotine+ is checked through its bridge plugin: it uses Nicotine+'s own native port checker when the version has one, and otherwise queries the Soulseek port-test service directly, so reachability works on current releases too. Enabled by default.

- **Pause and Resume Syncing**
  A **Pause Syncing** item in the tray menu temporarily stops sync cycles (including Media Manager imports) without changing any settings. While paused, **Sync Port Now** still runs a single cycle on demand. Syncing always resumes when the application restarts.

- **VPN Interface Mismatch Warning** *(qBittorrent and Nicotine+)*
  Shows a tray balloon tip and logs a warning if the client's network interface does not match the configured VPN provider, or if it is bound to all interfaces (which may cause traffic leaks). Transmission and Deluge report a bind address rather than an adapter name, so there is no name to compare and the check does not apply to them.

- **Stale Interface Binding Detection** *(qBittorrent only)*
  qBittorrent stores its network interface as an internal identifier as well as a name. When a VPN destroys and recreates its adapter the identifier stops resolving while the name still reads correctly, so qBittorrent listens on nothing and a restart cannot fix it - the value is in its own configuration. qbPortWeaver checks the identifier against qBittorrent's live adapter list each cycle and warns when it has gone stale. **Fix the network interface binding when it goes stale** re-applies it automatically, restoring the adapter you selected. Enabled by default; turn it off to be warned instead.

- **Port Update Notification**
  Optionally shows a tray balloon tip when the client's listening port is successfully updated to a new value. Enabled by default. Configurable via Settings → General.

- **Log Alert Notifications**
  When a warning or error is logged, a one-shot tray balloon appears; clicking it opens the log viewer at the latest issue. The **Show Logs** item and the tray tooltip show a running warning/error count, which clears when you open the log viewer or clear the logs.

- **Auto-Recovery**
  After a configurable number of consecutive failed cycles (VPN disconnected, or connected but no port assigned), recovery runs through a lightweight helper service (`qbPortWeaverHelper`, LocalSystem, no UAC): for ProtonVPN/PIA it restarts the VPN service and client; for a generic NAT-PMP gateway it cycles the adapter via netsh. Recovery is also held until the failures have *persisted* long enough, so a brief blip raced through by network-change re-syncs does not force a restart. A second, independent trigger runs the same recovery when port verification confirms the port closed for a set number of checks (on by default; requires **Check that the forwarded port is open after each sync**); it fires once and re-arms only when a scheduled check reports the port open again. Use with care on qBittorrent, where an idle client can report closed indefinitely. A **Test** button next to the Auto-recovery settings runs the recovery action on demand (after a confirmation), so the whole chain can be verified before a real failure needs it.

  Recovery is rate-limited while an internet connection cannot be confirmed, since restarting a VPN cannot restore a connection that is down upstream. Reachability is checked by pinging public DNS resolvers; when nothing answers, the first recovery of a streak still runs and later attempts are spaced out to 5, 10 and then 15 minutes until connectivity returns. It is deliberately a rate limit rather than a block: a VPN killswitch also blocks those pings while the tunnel is down, so refusing to recover outright would leave exactly the users who need a restart unable to get one.

- **Post-Update Command**
  Optionally run a custom command after a successful port update (fire-and-forget). See SampleSendMail.ps1 for an example of sending an email notification with status details.

- **Media Manager**
  Imports movie and TV episode files into Plex-compatible library folders each cycle, using [TMDB](https://www.themoviedb.org) for titles and years and Plex naming (`Title (Year).ext`, `Show (Year) - SxxExx.ext`, multi-episode `SxxExxExx`) via hardlink (falling back to copy across volumes), copy, or move. Beyond the `SxxExx` pattern, episodes in season subfolders with numbered filenames (`Show/Season N/01-Title.mp4`) are recognised too (English, French, Spanish, and Italian season indicators). It can create Plex subfolders (`Movies/Title (Year)/`, `TV Shows/Show (Year)/Season XX/`) and delete emptied source folders (including `.nfo`-only ones).

  A dedicated **Media Manager** dialog (tray menu → Media Manager) configures source and library folders, previews imports (**Scan Now**), and applies or corrects them (**Import Now**), highlighting uncertain TMDB matches. A free TMDB API key is required.

- **Tray Icon Interface**
  Runs quietly in the background with a system tray icon for quick access to logs, settings, and controls.

- **Tray Status Indicator**
  After each sync cycle the tray icon shows a colored status dot: **green** (ports aligned), **orange** (VPN not connected), **red** (error), **gray** (sync paused), or **no dot** (port sync disabled, or waiting during the startup grace period). Hovering over the icon displays the current port and status, and an unviewed log count if warnings or errors have occurred (e.g. "2 Warnings, 1 Error").

- **Status Panel**
  A **Status** window (tray menu → Show Status, or double-click the tray icon) shows the live sync chain at a glance: VPN provider and connection, an auto-recovery line covering both triggers (how close each is to firing, or why it is holding off), forwarded port, client and whether it is running, listening port (with an in-sync indicator), reachability (with how long ago it was last checked), the last sync time and result, and an estimate of the next sync. Color accents flag anything out of sync, closed, or in error. A **Recent Port Changes** list shows the latest port assignments, confirmed-closed results, and auto-recovery actions with timestamps, kept across restarts (right-click the list to clear it); port changes note their cause when a network change or a recovery prompted them. A **Statistics** section summarizes stability at a glance: the current port, how many times it changed today, and this session's sync and auto-recovery counts (manual recovery tests are not counted; right-click the section to reset the session counters). The panel refreshes after each cycle and updates its live timers every second. A **Sync Now** button runs an immediate cycle, **Pause/Resume** stops or restarts automatic cycles (the same toggle as the tray menu), **Test Port** checks reachability on demand, and **Run Diagnostics** health-checks the whole chain.

- **Diagnostics**
  A **Run Diagnostics** action (Status panel and tray menu) runs a read-only health check across the whole sync chain and shows a pass/warning/fail checklist with a fix hint for each step: configuration, helper service, internet connectivity, VPN connection and forwarded port, client running and reachable, ports in sync, interface binding, client settings, and outside reachability. The **internet connectivity** check reports whether this machine gets a reply when it pings public DNS resolvers, which is what auto-recovery uses to decide whether to attempt a restart immediately or space its attempts out. A warning here does not necessarily mean anything is broken: a network that filters ping, or a VPN killswitch blocking traffic while the tunnel is down, both produce it with the connection otherwise working. It is worth knowing either way: when auto-recovery is enabled this is what spaces out repeat attempts, and Diagnostics is the only place you can check it on demand rather than waiting for a recovery to be held. The **client settings** check looks for options in the client itself that undo the forwarded port - a randomised listening port, or the client's own UPnP/NAT-PMP forwarding. qbPortWeaver switches these off every time it sets the port, so a warning here means one was turned back on since; it names the option as the client's own settings screen labels it. On Transmission and Nicotine+ this is the only check that can see the problem: the client keeps reporting the correct port, so nothing looks wrong until it is next restarted. The same check also runs periodically during normal syncing and raises a tray warning when one of these options is switched on, so it is caught without having to run Diagnostics. **Re-run** refreshes it and **Copy Report** puts the results on the clipboard for a support request. It never changes the port or restarts anything.

- **Settings Dialog**
  All configuration options are editable through a dedicated Settings form (tray menu → Settings), organised into **General**, **Client**, **Auto-Recovery**, and **Extra** tabs, with inline descriptions and tooltips for each option. A **Detect** button on the General tab finds a running or installed client (qBittorrent, Transmission, Deluge, or Nicotine+) and fills in its selection and process details, asking you to choose when more than one is found. A second **Detect** button does the same for the VPN provider, selecting ProtonVPN or PIA when its service is present on the machine. NAT-PMP gateways are not machine-local and so cannot be detected; select **NAT-PMP** yourself if that is what you use.

- **Connection Test**
  Each client section in Settings has a **Test** button next to the URL. It checks the connection to the selected client using the values currently entered (no need to save first), then reports success along with the current listening port, or points you to the log if it cannot connect.

- **Log Viewer**
  Built-in log viewer (tray menu → Show Logs) displays the log file with color-coded entries by level (error, warn, info, debug) and follows new entries in real time. Includes a search bar with match highlighting and prev/next navigation, dedicated prev/next buttons to step between warnings and errors without affecting the search, toggle buttons to filter by log level, a subsystem filter to isolate entries from a specific component, a time filter to narrow the view to the last 15 minutes, hour or 24 hours, or to a custom start and end time, and a file picker to browse rotated backup files. The view is virtualized, so filters apply instantly and scrolling stays smooth even on very large logs, and the viewer keeps a low, steady memory footprint when left open for days. Adapts to the application color theme (System, Dark, or Light).

- **Consistent Theming**
  All windows - Settings, Status, Diagnostics, the log viewer, Media Manager, and the tray menu - follow your chosen color theme (System, Dark, or Light) uniformly, using the native Windows colors so text and surfaces stay legible in both light and dark mode. Confirmation and alert pop-ups are themed to match, so they no longer appear in bright white in dark mode.

- **Logging**
  Logs all operations and errors, with automatic log size management (20 MB per file, up to 5 files total). Clear logs directly from the tray menu.

- **Last-Run Status File**
  Writes a JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) after each sync cycle, exposing VPN port, client port, timestamps, and completion status for external scripts.

- **Automatic Update Checker & In-App Update**
  Checks GitHub for new releases on startup and every 12 hours, surfacing a newer version via an **Update available (X.Y.Z)** tray item and tooltip (the 12-hour check is non-intrusive; the startup form can be turned off under **Settings → General**). A **Check for Updates** item checks on demand and always reports a result. When an update is available, the update window offers **Download & Install** - it downloads the installer, runs it, and the app relaunches when the update finishes (falling back to the release page if anything goes wrong). The **About** dialog shows the current and latest version, update status, contributor links, and a **What's New** button.

- **Built-in User Guide**
  A **Help** item in the tray menu opens this user guide in a built-in viewer with a browsable table of contents, text search (Ctrl+F with match navigation), formatted headings, tables, and clickable links - no browser or internet connection needed.

- **Startup Option**
  Allows enabling or disabling automatic startup with Windows.

---

## Configuration

Settings are stored in the **Windows Registry** under `HKCU\Software\qbPortWeaver\settings` and are editable through the built-in Settings dialog (right-click the tray icon → **Settings**). Changes take effect on the next sync cycle.

Every option, with its meaning and default, is listed in the **[settings reference](docs/SETTINGS.md)**.

---

## Usage

### Startup

- The application starts minimized and runs in the system tray.
- On first run, open **Settings** from the tray menu to configure the application.

### Sync Loop

1. If VPN Provider is set to **Disabled**, the entire port sync is skipped and the cycle proceeds directly to the Media Manager step. This is useful when you only want automatic media importing without VPN port sync.
2. Checks whether the configured VPN provider is connected.
   - During the first 90 seconds after the app starts, if **Wait for VPN on startup** is enabled and the VPN is not yet connected (or has not assigned a port yet), the cycle waits quietly instead: the tray stays neutral, nothing is logged as a failure, the default-port fallback and auto-recovery are held, and the check repeats every 15 seconds (or your update interval, if that is shorter) so the port syncs promptly once the VPN comes up.
   - If **not connected** and **Default port** is 0 (or not a usable port number): skips the cycle and waits for the next interval.
   - If **not connected** and **Default port** is set: uses the default port as the target and continues.
   - If **Auto-Recovery** is enabled, the failed cycle count reaches the configured threshold, and the failures have persisted long enough (so a brief blip raced through by early re-syncs is ignored): automatically triggers recovery (via the helper Windows service) - for ProtonVPN and PIA (direct or NAT-PMP mode), restarts the VPN service and client; for NAT-PMP with a generic gateway, cycles the network adapter. Auto-recovery stops after 3 consecutive attempts that do not produce a forwarded port, and resumes automatically once one is read successfully.
3. Reads the VPN-assigned port from the configured provider (skipped if using the default port fallback). A port outside the usable range (1-65535) is logged as a warning and ignored, and the cycle continues as if no port had been reported. If port detection fails despite the VPN being connected, the failed cycle counter increments and auto-recovery may trigger.
   - If the provider instead reports that port forwarding is *unavailable* rather than simply failing to return a port, no port update is made, the reason is logged once as a warning, and the failed cycle counter is **not** incremented, so auto-recovery never runs for it. Restarting a VPN cannot create a forwarded port that is switched off in the provider's own settings or is not offered by the connected server region. The check repeats on the normal interval, so the port syncs as soon as the condition is corrected. Currently only PIA distinguishes these states (`Inactive` and `Unavailable`); a provider that simply reports no port is treated as an ordinary failure.
4. Checks if the configured client is running (optionally force-starts it if configured).
5. Connects to the client and retrieves the current listening port.
   - For qBittorrent and Nicotine+: also reads the bound network interface for mismatch detection.
6. *(qBittorrent and Nicotine+)* If **Warn when network interface doesn't match the VPN** is enabled: checks that the client's network interface matches the configured VPN provider and shows a tray warning if not. Transmission and Deluge report a bind address rather than an adapter name, so there is no name to compare and the check does not apply to them.
   *(qBittorrent only)* Also checks that qBittorrent's stored interface identifier still resolves to the adapter it names, and warns - or re-applies it, if **Fix the network interface binding when it goes stale** is enabled - when it does not. This check runs whatever the VPN provider is, since the binding can go stale without the VPN being involved.
   *(qBittorrent only)* Also checks the addresses on that adapter. The name and identifier above both survive a VPN reconnect unchanged, but the address on the adapter does not, and a client still listening on the previous one accepts no incoming connections while everything else looks healthy. If qBittorrent is bound to a specific address the adapter no longer has, it cannot be listening at all, so the address is corrected straight away when **Fix the network interface binding when it goes stale** is enabled (a warning otherwise). If instead qBittorrent is bound to all addresses on the adapter and the adapter's address simply changed, nothing is written yet: that is what an ordinary reconnect looks like and qBittorrent may well have coped. It is only acted on if the forwarded port then tests closed, in which case qBittorrent is nudged into listening on the new address before the VPN is restarted, since restarting the VPN cannot fix that and costs you the connection to find out. Your qBittorrent settings end up exactly as you had them, including a wildcard choice such as "All IPv4 addresses". That nudge runs only while **Check that the forwarded port is open after each sync**, **Trigger auto-recovery when port stays closed**, and **Fix the network interface binding when it goes stale** are all enabled (they are by default) - it is part of the port-closed recovery path, so it depends on the port check that detects the problem. The stale-address correction in the previous sentence needs only the last of the three.
7. If ports differ:
   - Updates the client's listening port.
   - Shows a tray balloon tip if **Show notification when port updates** is enabled.
   - Restarts the client if configured.
8. *(qBittorrent only)* If **Restart qBittorrent if connection status disconnects** is enabled (and qBittorrent was not already restarted in step 7): checks qBittorrent's connection status and restarts it if disconnected. After three consecutive restarts that leave it disconnected, further restarts are suspended until it reconnects.
9. If **Check that the forwarded port is open after each sync** is enabled and the VPN is connected: checks that the port is reachable from the Internet (after a port change and every 5th cycle otherwise). A closed result is re-tested on the next cycle before a warning is raised. If **Trigger auto-recovery when port stays closed** is enabled, repeated confirmed closed checks trigger auto-recovery (a VPN service restart, or adapter cycle for NAT-PMP), at most once until a scheduled check reports the port open again.
10. Writes the JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) and updates the tray icon and tooltip. If the port changed this cycle, the optional post-update command is then launched (fire-and-forget) - after the status file is written, so a script that reads it (e.g. `powershell -File "C:\path\to\SampleSendMail.ps1"`) sees this cycle's result rather than the previous one.
11. Waits for the configured interval before repeating. If a manual sync was triggered, the wait is shortened to 10 seconds.
12. In parallel with the wait, if **Media Manager** is enabled: scans the configured source folders, queries TMDB for each unrecognised title, and imports files into the library with Plex-compatible names. Runs as a fire-and-forget task so a slow library scan does not delay the next port sync cycle - if a previous import is still running when the next cycle starts, the new import is skipped to avoid pile-up. In **dry-run** mode no files are touched; use **Scan Now** in the Media Manager dialog to preview results first. Uncertain TMDB matches are skipped automatically and flagged for manual review in the dialog.

### Tray Menu Options

- **Sync Port Now** - triggers an immediate sync cycle, skipping the current wait interval (works while paused, running a single cycle)
- **Pause Syncing / Resume Syncing** - temporarily stops sync cycles, including Media Manager imports; the tray icon and tooltip show the paused state. Not persisted: syncing always resumes when the application restarts
- **Show Status** - opens the Status panel showing the live sync chain (also opened by double-clicking the tray icon); includes Sync Now, Pause/Resume, Test Port, and Run Diagnostics
- **Run Diagnostics** - runs a read-only health check of the whole sync chain and shows a pass/warning/fail report with fix hints
- **Show Logs** - opens the built-in Log Viewer; shows a warning/error count badge when unviewed entries exist
- **Clear Logs** - deletes all log files and starts a fresh log
- **Settings** - opens the Settings dialog
- **Media Manager** - opens the Media Manager dialog to configure source and library folders, preview imports (Scan Now), apply them (Import Now), and clear fingerprint caches (Clear Cache)
- **Check for Updates** - checks GitHub for a newer release on demand and reports the result (also shown when already up to date)
- **Help** - opens the user guide in a built-in viewer with a contents tree and search
- **About** - shows version info and update status
- **Start Automatically with Windows** - toggles the Windows startup registry entry
- **Exit** - shuts down the application

---

## Recommended Setup

### 1. BIOS Configuration

- Configure your PC BIOS to **auto-start after a power failure**, so the system recovers automatically from power outages.

### 2. Windows Auto-Logon

- Install [Sysinternals Autologon](https://learn.microsoft.com/en-us/sysinternals/downloads/autologon) to automatically log in to Windows after a reboot. This ensures your VPN client, peer-to-peer client, and qbPortWeaver all start without manual intervention.

### 3. ProtonVPN Configuration

- Enable **Split Tunneling** and route only your client through the VPN.
- Enable **Port Forwarding** (required for qbPortWeaver to work).
- Select a **P2P server**.
- Use **Proton WireGuard (UDP)** as the protocol, listed under **Proton Protocols** (the new protocol family introduced in ProtonVPN 5.1.5). If you run into connection trouble on it, **OpenVPN (UDP)** is still available as a fallback.
- Set ProtonVPN to **start with Windows**.
- Set `VPN Provider` to `ProtonVPN` in qbPortWeaver Settings (reads the forwarded port from the ProtonVPN log file).

> **Alternative:** ProtonVPN also supports NAT-PMP. If you prefer not to rely on log file parsing, set `VPN Provider` to `NAT-PMP` instead and select the ProtonVPN virtual adapter in the NAT-PMP Adapter dropdown. See the NAT-PMP Configuration section below.

> **Tunnel adapter names:** the **Proton Protocols** (Proton WireGuard, Proton Stealth) name the tunnel adapter `ProTUN` - so the recommended setup above gives you `ProTUN`. The earlier protocols name it `ProtonVPN` (standard WireGuard) or `ProtonVPN TUN` (OpenVPN). qbPortWeaver detects all of them automatically. If you switch protocols, reselect the active adapter wherever you have pinned it - the **NAT-PMP Adapter** dropdown and your client's **Network Interface** binding.

### 4. PIA Configuration (if using PIA instead of ProtonVPN)

- Enable **Split Tunneling** and route only your client through the VPN.
- Enable **Port Forwarding** in the PIA desktop client settings.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set PIA to **start with Windows**.
- Set `VPN Provider` to `PIA` in qbPortWeaver Settings.

> **Note:** PIA only assigns a forwarded port on server regions that support it, and only when **Port Forwarding** is enabled in its settings. If either of those is not true, qbPortWeaver logs a warning and waits rather than trying to recover, since no restart can produce a port that is not on offer. Enable debug logging to see the exact state PIA reports (`Attempting`, `Inactive`, `Unavailable`, `Failed`, or a port number).

### 5. NAT-PMP Configuration

NAT-PMP (RFC 6886) is a protocol for requesting port mappings directly from a gateway. qbPortWeaver supports it in two scenarios:

**With ProtonVPN (alternative to log file parsing):**
- ProtonVPN supports NAT-PMP natively on P2P servers. You can use this instead of the default log file approach.
- Enable **Port Forwarding** in ProtonVPN and connect to a P2P server - this enables NAT-PMP on the VPN gateway, which qbPortWeaver queries directly.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the **ProtonVPN virtual adapter** in the NAT-PMP Adapter dropdown.

> **Note:** With ProtonVPN, qbPortWeaver and the built-in port forwarding client both query the same gateway and receive the same external port - they share the same mapping rather than competing. qbPortWeaver uses that port to configure the client.

**With any other NAT-PMP capable VPN client or router:**
- The VPN gateway or router must support NAT-PMP (RFC 6886) with port forwarding enabled.
- Enable **port forwarding** in your VPN client or router settings.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the correct **network adapter** in the NAT-PMP Adapter dropdown - choose the virtual adapter created by your VPN client, or your LAN adapter if using a NAT-PMP capable router.

> If no adapter appears in the list, ensure the adapter is up and its gateway is responding to NAT-PMP, then click the **⟳** button to refresh without reopening Settings.

### 6. Client Configuration

#### qBittorrent

- **Disable UPnP/NAT-PMP** port mapping (Options → Connection) since the port is managed externally.
  > **Note:** qBittorrent's built-in NAT-PMP tries to open ports on your local router. qbPortWeaver's NAT-PMP mode is different - it queries your VPN gateway directly using the same protocol. Disabling qBittorrent's option does not affect qbPortWeaver.
- Enable **Anonymous Mode** (Options → BitTorrent).
- Enable **Web UI** (Options → Web UI) and configure a username and password matching your qbPortWeaver Settings.
- Bind the **network interface** to your VPN adapter (Options → Advanced → Network Interface) to prevent traffic leaks outside the VPN.
  > **Note:** If you change ProtonVPN protocols, reselect the adapter here - the tunnel adapter is named `ProTUN` on the Proton Protocols (Proton WireGuard, Proton Stealth), or `ProtonVPN` / `ProtonVPN TUN` on the earlier ones (standard WireGuard / OpenVPN). A stale binding triggers the interface mismatch warning.
- Set qBittorrent to **start with Windows**.

#### Transmission

- Enable **RPC** in Transmission preferences and set a username and password.
- Use the **remote session** (connect via `http://localhost:9091` in qbPortWeaver Settings). Do not use Transmission's local session; when qbPortWeaver restarts the process, the RPC endpoint is the only reliable way to communicate across restarts.
- If Transmission is installed as a **Windows service**, qbPortWeaver detects it automatically.
- If running as a **user-space process** (e.g. Transmission Qt), set the Process name (e.g. `transmission-qt`) and the Executable path so qbPortWeaver can restart it after a port change.
- **Enable your VPN client's killswitch** to prevent traffic leaks. Transmission only allows binding to an IP address (not an adapter name), and the IP assigned by the VPN typically rotates on reconnection - making bind-address rules brittle. The VPN killswitch blocks all traffic when the tunnel is down, regardless of what address Transmission is bound to. Both ProtonVPN and PIA expose this option in their desktop clients.

#### Deluge

- Enable the **Web UI plugin** (Preferences → Plugins) and set a password.
- Set the URL in qbPortWeaver Settings to match the Web UI address (default `http://127.0.0.1:8112`).
- Set the Process name (e.g. `deluge`) and Executable path so qbPortWeaver can restart it after a port change.
- Disable **UPnP** and **NAT-PMP** in Deluge preferences (Preferences → Network) since the port is managed externally.
- **Enable your VPN client's killswitch** to prevent traffic leaks. Deluge only allows binding to an IP address (not an adapter name), and the IP assigned by the VPN typically rotates on reconnection - making bind-address rules brittle. The VPN killswitch blocks all traffic when the tunnel is down, regardless of what address Deluge is bound to. Both ProtonVPN and PIA expose this option in their desktop clients.

#### Nicotine+

Nicotine+ has no remote-control interface, so qbPortWeaver talks to it through a small bridge
plugin. Everything below is a one-off; after it, port sync is fully automatic and the port changes
without restarting Nicotine+.

1. In qbPortWeaver **Settings → General**, set **Client** to **Nicotine+** (or click **Detect**).
2. Open the **Client** tab - it now shows the Nicotine+ section - and click **Install Plugin**. If
   Nicotine+ is closed, accept the offer to enable it too - your Nicotine+ configuration is backed
   up first and only the plugin list is touched. If Nicotine+ is running, enable **qbPortWeaver
   Bridge** in **Preferences → Plugins**; no restart needed.
3. Start Nicotine+ if it is not running, then click **⟳** next to the Plugin token. The address and
   token fill in automatically.
4. Use **Test** to confirm, then save.

Notes:

- **Do not launch Nicotine+ with `--port`.** That option overrides the configured port for the life
  of the process and nothing can change it - not this plugin, not Nicotine+'s own preferences dialog.
  qbPortWeaver reports it and stops trying.
- The plugin turns Nicotine+'s own **UPnP** off when it sets the port, so its port mapping cannot
  fight the externally managed one.
- **Portable installations** work as long as the Executable path is set, which is how qbPortWeaver
  finds the `portable\data` folder beside it.
- If you run Nicotine+ with a custom data folder (`-c` or `--user-data`), qbPortWeaver cannot work
  out where to look. Run `/qbpw-connection-file` in Nicotine+ and enter the address and token by
  hand - both are stable across restarts.
- **Enable your VPN client's killswitch.** Nicotine+ can bind to a named adapter, and qbPortWeaver
  warns when that does not match your VPN, but the killswitch is what actually stops a leak.

### 7. qbPortWeaver

- Enable **Start Automatically with Windows** from the tray menu.
- On first run, open **Settings** from the tray menu, select your client and your VPN provider (or click **Detect** on either row to find them automatically), and enter the connection credentials and preferences.
- Use the **Test** button next to the client URL to confirm the connection works before saving.

---

## Logging

- All actions and errors are logged to `%LocalAppData%\qbPortWeaver\qbPortWeaver.log`.
- Log files are automatically rotated when exceeding **20 MB**, keeping up to 5 files (current + 4 backups).
- Open the **Log Viewer** from the tray menu (Show Logs). It shows color-coded entries (red for errors, gold for warnings, blue for info, orange for debug) and tails new entries live. Use the search bar to find and highlight matches with prev/next navigation, the level filter buttons to show only the levels you care about, the subsystem dropdown to isolate entries from a specific component, the time filter to narrow the view to a recent window or a custom start and end time (useful for reading back over an incident that has already passed), or the file picker to browse rotated backup files. The viewer adapts to the application color theme configured in Settings.

---

## Error Handling

The application is designed to always recover. A failing cycle never crashes the app; errors are logged and the loop retries on the next interval.

### Port Sync

- If the VPN provider is not connected and no default port is configured, the cycle is skipped and the issue is logged.
- If the VPN provider is not connected and a default port is configured, the default port is applied instead.
- If the VPN port cannot be determined, the issue is logged and the update is skipped. If Auto-Recovery is enabled, repeated failures trigger automatic recovery, up to the consecutive-attempt cap described under Auto-Recovery.
- If the VPN provider reports that port forwarding is unavailable (switched off in its settings, or not offered by the connected region), the reason is logged once and the update is skipped. This is a configuration condition rather than a fault, so it never triggers automatic recovery.
- If the client is not running and cannot be force-started or updated, errors are logged and the loop continues after the next interval.

### Media Manager

- If a TMDB API call fails (network error, invalid key), the file is skipped and the error is logged. Other files in the same scan continue processing.
- If a folder is inaccessible (permissions, or a network share that is offline or slow to respond), it is detected quickly without stalling the cycle. A source folder is skipped with a warning while the others are still processed; if a library folder is unreachable, the library index build is skipped that cycle and retried on the next (rather than committing a partial index), resuming automatically once the share is reachable.
- If file import fails (I/O error, disk full), the individual file is skipped. The scan continues with the next file.
- If the fingerprint cache is corrupt or unreadable, it is discarded and rebuilt from scratch on the next scan.

### UI

- If the Settings, Media Manager, or About dialog encounters an error, it is displayed in the status label or logged. The main application loop is never affected.
- If the Log Viewer cannot read the log file, it degrades gracefully without crashing.

### Failures that report themselves

Some problems sit outside the sync loop and used to pass unnoticed. Each of these now says so:

- **The log file cannot be written** (disk full, or permissions on the qbPortWeaver folder). Reported as a tray message rather than a log entry, for the obvious reason. It is announced once per episode and re-arms after a write succeeds, so a persistently failing log does not notify on every entry.
- **The Windows startup entry could not be updated** after the application moved. Logged as a warning naming where the entry still points, since the consequence is that qbPortWeaver may not start at logon, or may start an older copy. Write access to that registry key is often restricted by group policy on managed machines.
- **The port history file could not be written**, which means Recent Port Changes stops updating.
- **The helper service is older than the application**, usually after an upgrade where the service was left behind. Recovery still runs, but newer behaviour may be missing; reinstall qbPortWeaver to update it. The reverse case, a helper newer than the app after a downgrade, is reported separately so the advice is not misleading.

---

## Documentation

- **[Settings reference](docs/SETTINGS.md)** - every option in the Settings dialog, with its meaning and default: General, the four clients, Auto-Recovery, Extra, and the Media Manager.
- **[Sync cycle](docs/SYNC-CYCLE.md)** - how a cycle actually runs: VPN detection, the two auto-recovery triggers and every gate that holds them back, client interaction and the interface checks, the JSON status file consumed by external scripts, diagnostics, and a full method call map. Read this before changing `PortSyncService`.
- **[Nicotine+ bridge plugin](plugins/qbpw_nicotine_bridge/README.md)** - what the plugin installs, how qbPortWeaver discovers it, its chat commands and HTTP API, what happens when Nicotine+ was started with `--port`, and its security model.

---

## Contributing

Pull requests are welcome. See **[CONTRIBUTING.md](CONTRIBUTING.md)** for the branch and
release strategy, branch naming, and the release workflow.

---

## Changelog

Release notes for every version are on the [GitHub Releases](https://github.com/martsg666/qbPortWeaver/releases) page.

Versions before 2.2.0 predate the current architecture - four clients, NAT-PMP, the helper
service and the Media Manager all arrived later - and are recorded in the git history rather
than here.

---

## License

Free of use and distribution. No warranty provided.

## Author
Developed by martsg666
