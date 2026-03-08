# qbPortWeaver

## Overview

**qbPortWeaver** is a Windows application designed to synchronize the listening port of **qBittorrent** with the port assigned by your VPN provider (**ProtonVPN**, **Private Internet Access**, or any **NAT-PMP capable VPN gateway or router**).
This ensures your torrent client always uses the VPN-provided port, improving privacy and connectivity.

The application runs in the system tray, manages configuration and logging, and automatically updates qBittorrent's port when changes are detected.

---

## Requirements

- Windows 10/11 (x64)
- ProtonVPN, Private Internet Access (PIA), or any NAT-PMP capable VPN or router with port forwarding enabled
- qBittorrent installed with Web UI enabled

---

## Features

- **Tray Icon Interface**
  Runs quietly in the background with a system tray icon for quick access to logs, settings, and controls.

- **Tray Status Indicator**
  After each sync cycle the tray icon shows a colored status dot: **green** (ports aligned), **orange** (VPN not connected), **red** (error). Hovering over the icon displays the current port and status at a glance, without opening the log file.

- **Automatic Port Synchronization**
  Detects the current VPN port and updates qBittorrent's listening port automatically.

- **Multi-VPN Support**
  Supports **ProtonVPN** (via log file parsing or NAT-PMP), **Private Internet Access** (via `piactl` CLI), and any **NAT-PMP capable VPN gateway or router** (via RFC 6886 UDP port mapping). Configurable through the Settings dialog.

- **Settings Dialog**
  All configuration options are editable through a dedicated Settings form (tray menu → Settings), with inline descriptions and tooltips for each option.

- **Log Viewer**
  Built-in log viewer (tray menu → Show Logs, or double-click the tray icon) displays the log file with color-coded entries by level (error, warn, info, debug) and follows new entries in real time. Supports Windows dark mode.

- **Logging**
  Logs all operations and errors, with automatic log size management (5 MB per file, up to 3 rotated files). Clear logs directly from the tray menu.

- **qBittorrent Control**
  Authenticates with qBittorrent's Web API, updates preferences, and restarts the client if required.

- **Last-Run Status File**
  Writes a JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) after each sync cycle, exposing VPN port, qBittorrent port, timestamps, and completion status for external scripts.

- **Restart qBittorrent After Port Change**
  Optionally restart qBittorrent after updating the port to ensure changes take effect immediately.

- **Force Start qBittorrent**
  Optionally force start qBittorrent if it is not running.

- **Default Port Fallback**
  When VPN is not connected, optionally sets qBittorrent's listening port to a configured default. Useful if you have a port forwarded in your router for direct connections without VPN.

- **VPN Interface Mismatch Warning**
  Shows a tray balloon tip and logs a warning if qBittorrent's network interface does not match the configured VPN provider, or if qBittorrent is bound to all interfaces (which may cause traffic leaks).

- **Restart qBittorrent on Disconnect**
  Optionally restart qBittorrent when its connection status changes to disconnected. Requires the Executable and Process name to be configured.

- **Post-Update Command**
  Optionally run a custom command after a successful port update (fire-and-forget). See SampleSendMail.ps1 for an example of sending an email notification with status details.

- **Automatic Update Checker**
  Checks GitHub for new releases on startup and every 12 hours, and offers to open the download page. The **About** dialog (tray menu → About) also shows the current and latest version, update status, and contributor links.

- **Startup Option**
  Allows enabling or disabling automatic startup with Windows.

---

## Configuration

Settings are stored in the **Windows Registry** under `HKCU\Software\qbPortWeaver\Settings` and are editable through the built-in Settings dialog (right-click the tray icon → **Settings**).

On first run, all settings are initialized with sensible defaults.

### Available Settings

| Setting | Description | Default |
|---|---|---|
| VPN Provider | `ProtonVPN`, `PIA`, or `NAT-PMP` | `ProtonVPN` |
| NAT-PMP Adapter | Network adapter to use for NAT-PMP port mapping (only enabled when NAT-PMP is selected) | — |
| Update interval | How often to check and sync the port (seconds) | `180` |
| URL | qBittorrent Web API URL | `http://127.0.0.1:8080` |
| Username | qBittorrent Web UI username | `admin` |
| Password | qBittorrent Web UI password | — |
| Executable | Path to qBittorrent executable | `C:\Program Files\qBittorrent\qbittorrent.exe` |
| Process name | qBittorrent process name (used to detect if it's running) | `qbittorrent` |
| Restart after port change | Restart qBittorrent after updating the port (recommended) | `True` |
| Force start if not running | Automatically launch qBittorrent if it is not running | `False` |
| Default port (0 = disabled) | Fallback port to apply when VPN is not connected | `0` |
| Warn on interface mismatch | Warn if qBittorrent's network interface doesn't match the VPN | `True` |
| Restart on disconnect | Restart qBittorrent when its connection status changes to disconnected (requires Executable and Process name) | `False` |
| Post-update command | Command to run after a successful port update (leave empty to disable) | — |
| Debug logging | Enable verbose debug logging to the log file | `False` |

---

## Usage

### Startup

- The application starts minimized and runs in the system tray.
- On first run, open **Settings** from the tray menu to configure the application.

### Synchronization Loop

1. Checks whether the configured VPN provider is connected.
   - If **not connected** and **Default port** is 0: skips the cycle and waits for the next interval.
   - If **not connected** and **Default port** is set: uses the default port as the target and continues.
2. Reads the VPN-assigned port from the configured provider (skipped if using the default port fallback).
3. Checks if qBittorrent is running (optionally force starts it if configured).
4. Authenticates with qBittorrent and retrieves the current listening port and network interface.
5. If **Warn on interface mismatch** is enabled: checks that qBittorrent's network interface matches the configured VPN provider and shows a tray warning if not.
6. If ports differ:
   - Updates qBittorrent's port.
   - Restarts qBittorrent if configured.
   - Runs the optional post-update command if configured. e.g., `powershell -File "C:\path\to\SampleSendMail.ps1"`
7. If **Restart on disconnect** is enabled (and qBittorrent was not already restarted in step 6): checks qBittorrent's connection status and restarts it if disconnected.
8. Writes the JSON status file (`%LocalAppData%\qbPortWeaver\qbPortWeaver.status.json`) and updates the tray icon and tooltip.
9. Waits for the configured interval before repeating.

### Tray Menu Options

- **Synchronize Port Now** — triggers an immediate sync cycle, skipping the current wait interval
- **Show Logs** — opens the built-in Log Viewer (also opened by double-clicking the tray icon)
- **Clear Logs** — deletes all log files and starts a fresh log
- **Settings** — opens the Settings dialog
- **About** — shows version info and update status
- **Start Automatically with Windows** — toggles the Windows startup registry entry
- **Exit** — shuts down the application

---

## Recommended Setup

### 1. BIOS Configuration

- Configure your PC BIOS to **auto-start after a power failure**, so the system recovers automatically from power outages.

### 2. Windows Auto-Logon

- Install [Sysinternals Autologon](https://learn.microsoft.com/en-us/sysinternals/downloads/autologon) to automatically log in to Windows after a reboot. This ensures your VPN client, qBittorrent, and qbPortWeaver all start without manual intervention.

### 3. ProtonVPN Configuration

- Enable **Split Tunneling** and route only qBittorrent through the VPN.
- Enable **Port Forwarding** (required for qbPortWeaver to work).
- Select a **P2P server**.
- Enable **NetShield**.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set ProtonVPN to **start with Windows**.
- Set `VPN Provider` to `ProtonVPN` in qbPortWeaver Settings (reads the forwarded port from the ProtonVPN log file — recommended default).

> **Alternative:** ProtonVPN also supports NAT-PMP. If you prefer not to rely on log file parsing, set `VPN Provider` to `NAT-PMP` instead and select the ProtonVPN virtual adapter in the NAT-PMP Adapter dropdown. See the NAT-PMP Configuration section below.

### 4. PIA Configuration (if using PIA instead of ProtonVPN)

- Enable **Split Tunneling** and route only qBittorrent through the VPN.
- Enable **Port Forwarding** in the PIA desktop client settings.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set PIA to **start with Windows**.
- Set `VPN Provider` to `PIA` in qbPortWeaver Settings.

### 5. NAT-PMP Configuration

NAT-PMP (RFC 6886) is a protocol for requesting port mappings directly from a gateway. qbPortWeaver supports it in two scenarios:

**With ProtonVPN (alternative to log file parsing):**
- ProtonVPN supports NAT-PMP natively on P2P servers. You can use this instead of the default log file approach.
- Enable **Port Forwarding** in ProtonVPN and connect to a P2P server — this enables NAT-PMP on the VPN gateway, which qbPortWeaver queries directly.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the **ProtonVPN virtual adapter** in the NAT-PMP Adapter dropdown.

> **Note:** With ProtonVPN, qbPortWeaver and the built-in port forwarding client both query the same gateway and receive the same external port — they share the same mapping rather than competing. qbPortWeaver uses that port to configure qBittorrent.

**With any other NAT-PMP capable VPN client or router:**
- The VPN gateway or router must support NAT-PMP (RFC 6886) with port forwarding enabled.
- Enable **port forwarding** in your VPN client or router settings.
- Set `VPN Provider` to `NAT-PMP` in qbPortWeaver Settings.
- Select the correct **network adapter** in the NAT-PMP Adapter dropdown — choose the virtual adapter created by your VPN client, or your LAN adapter if using a NAT-PMP capable router.

> If no adapter appears in the list, ensure the adapter is up and its gateway is responding to NAT-PMP, then click the **↻** button to refresh without reopening Settings.

### 6. qBittorrent Configuration

- **Disable UPnP/NAT-PMP** port mapping (Options > Connection) since the port is managed externally.
  > **Note:** qBittorrent's built-in NAT-PMP tries to open ports on your local router. qbPortWeaver's NAT-PMP mode is different — it queries your VPN gateway directly using the same protocol. Disabling qBittorrent's option does not affect qbPortWeaver.
- Enable **Anonymous Mode** (Options > BitTorrent).
- Enable **Web UI** (Options > Web UI) and configure a username and password matching your qbPortWeaver Settings.
- Bind the **network interface** to your VPN adapter (Options > Advanced > Network Interface) to prevent traffic leaks outside the VPN.
- Set qBittorrent to **start with Windows**.

### 7. qbPortWeaver

- Enable **Start Automatically with Windows** from the tray menu.
- On first run, open **Settings** from the tray menu and enter your qBittorrent Web UI credentials and preferences.

---

## Logging

- All actions and errors are logged to `%LocalAppData%\qbPortWeaver\qbPortWeaver.log`.
- Log files are automatically rotated when exceeding **5 MB**, keeping up to 3 files (current + 2 backups).
- Open the **Log Viewer** from the tray menu (Show Logs) or by double-clicking the tray icon. It shows color-coded entries (red for errors, gold for warnings, blue for info, orange for debug) and tails new entries live. It follows your Windows dark/light theme preference.

---

## Error Handling

- If the VPN provider is not connected and no default port is configured, the cycle is skipped and the issue is logged.
- If the VPN provider is not connected and a default port is configured, the default port is applied instead.
- If the VPN port cannot be determined, the issue is logged and the update is skipped.
- If qBittorrent is not running and cannot be force started or updated, errors are logged and the loop continues after the next interval.

---

## Contributing

### Branch and Release Strategy

**`master`** always reflects the latest published release. Do not commit directly to `master`.

#### Branch naming

| Purpose | Base branch | Name pattern |
|---|---|---|
| Release | Previous release branch | `2.x.y` |
| Hotfix | Corresponding release branch | `fix/<description>` |
| Feature | Corresponding release branch | `feature/<description>` |

#### Workflow diagram

```
master  ──────────────────────────────────────────────────────────────► (always latest release)
           │                                                          ▲
           │  git checkout -b 2.5.0 origin/2.4.0                    │ git merge --no-ff 2.5.0
           ▼                                                          │
2.5.0   ──┬───────────────────────────────────────── git tag v2.5.0 ─┘
           │                                                  │
           ├── fix/some-bug   → PR → merge into 2.5.0         └─► CI/CD pipeline triggers
           └── feature/new-ui → PR → merge into 2.5.0               ├─ dotnet publish (self-contained win-x64)
                                                                      ├─ WiX MSI build
                                                                      ├─ GitHub Release created + MSI uploaded
                                                                      └─ Chocolatey package pushed
```

#### Workflow steps

1. **Create a release branch** from the previous release branch:
   ```
   git checkout -b 2.5.0 origin/2.4.0
   git push -u origin 2.5.0
   ```

2. **Create fix or feature branches** off the release branch and open a PR targeting it:
   ```
   git checkout -b fix/my-fix origin/2.5.0
   # or
   git checkout -b feature/my-feature origin/2.5.0
   ```

3. **Tag the release branch** once all testing is complete — this triggers the pipeline:
   ```
   git tag v2.5.0 origin/2.5.0
   git push origin v2.5.0
   ```
   Pushing the tag automatically triggers the **Build, Release, and Publish** pipeline, which builds the app, compiles the MSI installer, creates the GitHub Release, and publishes to Chocolatey.

4. **Merge the release branch into `master`** after the pipeline completes successfully:
   ```
   git checkout master
   git merge --no-ff 2.5.0
   git push origin master
   ```

5. **Do not delete release branches.** They serve as the base for future hotfixes. If a branch is accidentally deleted it can be reconstructed from its tag:
   ```
   git checkout -b 2.5.0 v2.5.0
   git push origin 2.5.0
   ```

---

## Extensibility

The modular architecture makes it easy to:

- Add support for other VPN providers
- Integrate additional torrent clients
- Extend configuration or logging features

---

## Changelog

### v2.2.0 and later — see [GitHub Releases](https://github.com/martsg666/qbPortWeaver/releases)

### v2.0.0
- **Tray status indicator**: the tray icon now shows a colored dot (green / orange / red) reflecting the last sync result, and the tooltip shows the current port and status without opening the log file
- Settings are now stored in the **Windows Registry** (`HKCU\Software\qbPortWeaver\Settings`). Existing settings are automatically migrated from the INI file on first run
- The qBittorrent **password is now encrypted** in the registry using Windows DPAPI. Existing plaintext passwords (from INI migration or older installs) are transparently re-encrypted on first read
- New **Settings** dialog (tray menu → Settings): all options are now editable in a dedicated form with inline descriptions and tooltips, replacing the previous Notepad shortcut
- Tray balloon tip and log warning when qBittorrent's network interface doesn't match the configured VPN provider, or when bound to all interfaces (potential traffic leak). Configurable via **Warn on interface mismatch** in Settings

### v1.7.0
- **Last-run status file** (`qbPortWeaver.status.json`) written after each sync cycle to `%LocalAppData%\qbPortWeaver\`. Useful for external scripts or monitoring — exposes VPN port, qBittorrent port, port change flag, timestamp, and status message
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
- Added **Synchronize Port Now** tray menu option for on-demand port sync

### v1.0.0
- Initial release

---

## License

Free of use and distribution. No warranty provided.

## Author
Developed by martsg666
