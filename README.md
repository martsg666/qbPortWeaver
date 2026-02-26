# qbPortWeaver 2.2.0

## Overview

**qbPortWeaver** is a Windows application designed to synchronize the listening port of **qBittorrent** with the port assigned by your VPN provider (**ProtonVPN** or **Private Internet Access**).
This ensures your torrent client always uses the VPN-provided port, improving privacy and connectivity.

The application runs in the system tray, manages configuration and logging, and automatically updates qBittorrent's port when changes are detected.

---

## Features

- **Tray Icon Interface**
  Runs quietly in the background with a system tray icon for quick access to logs, settings, and controls.

- **Tray Status Indicator**
  After each sync cycle the tray icon shows a colored status dot: **green** (ports aligned), **orange** (VPN not connected), **red** (error). Hovering over the icon displays the current port and status at a glance, without opening the log file.

- **Automatic Port Synchronization**
  Detects the current VPN port and updates qBittorrent's listening port automatically.

- **Multi-VPN Support**
  Supports **ProtonVPN** (via log file parsing) and **Private Internet Access** (via `piactl` CLI). Configurable through the Settings dialog.

- **Settings Dialog**
  All configuration options are editable through a dedicated Settings form (tray menu → Settings), with inline descriptions and tooltips for each option.

- **Logging**
  Logs all operations and errors, with automatic log size management. Clear logs directly from the tray menu.

- **qBittorrent Control**
  Authenticates with qBittorrent's Web API, updates preferences, and restarts the client if required.

- **Last-Run Status File**
  Writes a JSON status file (`qbPortWeaver.status.json`) after each sync cycle, exposing VPN port, qBittorrent port, timestamps, and completion status for external scripts.

- **Post-Update Command**
  Optionally run a custom command after a successful port update (fire-and-forget). See SampleSendMail.ps1 for an example of sending an email notification with status details.

- **VPN Interface Mismatch Warning**
  Shows a tray balloon tip and logs a warning if qBittorrent's network interface does not match the configured VPN provider, or if qBittorrent is bound to all interfaces (which may cause traffic leaks).

- **Default Port Fallback**
  When VPN is not connected, optionally sets qBittorrent's listening port to a configured default. Useful if you have a port forwarded in your router for direct connections without VPN.

- **Force Start qBittorrent**
  Optionally force start qBittorrent if it is not running.

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
| VPN Provider | `ProtonVPN` or `PIA` | `ProtonVPN` |
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
| Post-update command | Command to run after a successful port update (leave empty to disable) | — |
| Debug logging | Enable verbose debug logging to the log file | `False` |

---

## Usage

### Startup

- The application starts minimized and runs in the system tray.

### Configuration

- On first run, all settings are initialized with defaults.
- Open **Settings** from the tray menu to configure the application.

### Synchronization Loop

1. Checks whether the configured VPN provider is connected.
2. Reads the VPN-assigned port from the configured provider.
3. Checks if qBittorrent is running (optionally force starts it if configured).
4. Authenticates with qBittorrent and retrieves the current listening port.
5. If ports differ:
   - Updates qBittorrent's port.
   - Restarts qBittorrent if configured.
   - Runs the optional post-update command if configured. ex: `powershell -File "C:\Dev\SendMail.ps1"`
6. Waits for the configured interval before repeating.

### Tray Menu Options

- Synchronize Port Now
- Show Logs
- Clear Logs
- Settings
- About
- Start Automatically with Windows
- Exit

---

## Logging

- All actions and errors are logged to `qbPortWeaver.log`.
- Log files are automatically rotated when exceeding **5 MB**, keeping up to 3 files (current + 2 backups).

---

## Error Handling

- If the VPN provider is not connected or the port cannot be determined, the issue is logged and the update is skipped.
- If qBittorrent is not running and cannot be force started or updated, errors are logged and the loop continues after the next interval.

---

## Extensibility

The modular architecture makes it easy to:

- Add support for other VPN providers
- Integrate additional torrent clients
- Extend configuration or logging features

---

## Example Workflow

1. User starts **qbPortWeaver**
2. Application loads settings from the registry
3. VPN provider status is checked and port retrieved
4. qBittorrent port is compared and updated if needed
5. qBittorrent restarts (if enabled)
6. Post-update command runs (if configured)
7. Actions are logged
8. Application waits until the next interval

---

## Requirements

- Windows OS
- ProtonVPN or Private Internet Access (PIA) installed and running
- qBittorrent installed with Web API enabled

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
- Set `VPN Provider` to `ProtonVPN` in qbPortWeaver Settings.

### 4. PIA Configuration (if using PIA instead of ProtonVPN)

- Enable **Split Tunneling** and route only qBittorrent through the VPN.
- Enable **Port Forwarding** in the PIA desktop client settings.
- Use **OpenVPN (UDP)** as the protocol to avoid DNS resolution issues that can occur with WireGuard.
- Set PIA to **start with Windows**.
- Set `VPN Provider` to `PIA` in qbPortWeaver Settings.

### 5. qBittorrent Configuration

- **Disable UPnP/NAT-PMP** port mapping (Options > Connection) since the port is managed by your VPN provider.
- Enable **Anonymous Mode** (Options > BitTorrent).
- Enable **Web UI** (Options > Web UI) and configure a username and password matching your qbPortWeaver Settings.
- Bind the **network interface** to your VPN adapter (Options > Advanced > Network Interface) to prevent traffic leaks outside the VPN.
- Set qBittorrent to **start with Windows**.

### 6. qbPortWeaver

- Enable **Start Automatically with Windows** from the tray menu.
- On first run, open **Settings** from the tray menu and enter your qBittorrent Web UI credentials and preferences.

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

#### Workflow

1. **Create a release branch** from the appropriate upstream:
   ```
   git checkout -b 2.3.0 origin/2.2.0
   git push -u origin 2.3.0
   ```

2. **Create fix or feature branches** off the release branch and open a PR targeting it:
   ```
   git checkout -b fix/my-fix origin/2.3.0
   # or
   git checkout -b feature/my-feature origin/2.3.0
   ```

3. **Tag the release branch** once all testing is complete — this triggers the pipeline:
   ```
   git tag v2.3.0 origin/2.3.0
   git push origin v2.3.0
   ```
   Pushing the tag automatically triggers the **Build, Release, and Publish** pipeline, which builds the app, compiles the NSIS installer, creates the GitHub Release, and publishes to Chocolatey.

4. **Merge the release branch into `master`** after the pipeline completes successfully:
   ```
   git checkout master
   git merge --no-ff 2.3.0
   git push origin master
   ```

5. **Do not delete release branches.** They serve as the base for future hotfixes. If a branch is accidentally deleted it can be reconstructed from its tag:
   ```
   git checkout -b 2.3.0 v2.3.0
   git push origin 2.3.0
   ```

---

## Changelog

### 2.2.0
- Removed legacy INI file migration code — v2.0.0 is the required baseline for upgrading
- Internal code cleanup and consistency improvements across all modules
- New **About** dialog (tray menu → About): shows current and latest GitHub release, update status, and contributor links
- Update checker now also runs every 12 hours in the background, not only on startup
- Distributed as a **self-contained single-file executable** — no .NET runtime installation required
- Automated **CI/CD pipeline** via GitHub Actions: pushing a `v*` tag builds the app, compiles the NSIS installer, and publishes a GitHub Release automatically
- Available on the **Chocolatey Community Repository**: `choco install qbportweaver`

### 2.0.0
- **Tray status indicator**: the tray icon now shows a colored dot (green / orange / red) reflecting the last sync result, and the tooltip shows the current port and status without opening the log file
- Settings are now stored in the **Windows Registry** (`HKCU\Software\qbPortWeaver\Settings`). Existing settings are automatically migrated from the INI file on first run
- The qBittorrent **password is now encrypted** in the registry using Windows DPAPI. Existing plaintext passwords (from INI migration or older installs) are transparently re-encrypted on first read
- New **Settings** dialog (tray menu → Settings): all options are now editable in a dedicated form with inline descriptions and tooltips, replacing the previous Notepad shortcut
- Tray balloon tip and log warning when qBittorrent's network interface doesn't match the configured VPN provider, or when bound to all interfaces (potential traffic leak). Configurable via **Warn on interface mismatch** in Settings

### 1.7.0
- **Last-run status file** (`qbPortWeaver.status.json`) written after each sync cycle to `%LocalAppData%\qbPortWeaver\`. Useful for external scripts or monitoring — exposes VPN port, qBittorrent port, port change flag, timestamp, and status message
- **Clear Logs** option in the tray menu
- Improved error messages for qBittorrent Web API failures, including wrong credentials, unreachable Web UI, and HTTP errors
- Fixed a PIA issue where `piactl.exe` could hang indefinitely if it failed to return a port

### 1.6.1
- New **Default port** option: set a fallback listening port when the VPN is not connected (0 = disabled). Useful if you have a port forwarded on your router for direct connections
- Fixed PIA VPN detection failing in certain installation configurations

### 1.6.0
- Added **Private Internet Access (PIA)** VPN support via `piactl` CLI alongside ProtonVPN
- New `vpnProvider` setting to switch between ProtonVPN and PIA. Changing the provider takes effect on the next sync cycle without restarting
- New `debugMode` setting for verbose debug logging
- **Breaking change:** settings `ForceStartqBittorrent` and `PostUpdateCmd` renamed to `forceStartqBittorrent` and `postUpdateCmd`

### 1.5.0
- **Automatic update checker**: notifies on startup when a new release is available on GitHub

### 1.4.0
- New **Force start** option: automatically launches qBittorrent if it is not running during a sync cycle

### 1.3.0
- New **Post-update command** option: run a custom script or command after a successful port update (runs in the background, never blocks the sync loop)

### 1.2.1
- Fixed a crash on Windows shutdown, restart, or logoff

### 1.2.0
- Log rotation: keeps up to 3 log files (5 MB each) instead of overwriting
- Various stability improvements

### 1.1.0
- Added **Synchronize Port Now** tray menu option for on-demand port sync

### 1.0.0
- Initial release

---

## License

Free of use and distribution. No warranty provided.

## Author
Developed by @martsg666