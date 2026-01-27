# qbPortWeaver 1.0.0

## Overview

**qbPortWeaver** is a Windows application designed to synchronize the listening port of **qBittorrent** with the port assigned by **ProtonVPN**.
This ensures your torrent client always uses the VPN-provided port, improving privacy and connectivity.

The application runs in the system tray, manages configuration and logging, and automatically updates qBittorrent’s port when changes are detected.

---

## Features

- **Tray Icon Interface**  
  Runs quietly in the background with a system tray icon for quick access to logs, configuration, and controls.

- **Automatic Port Synchronization**  
  Detects the current ProtonVPN port and updates qBittorrent’s listening port automatically.

- **INI Configuration Management**  
  Reads and writes application settings from an INI configuration file.

- **Logging**  
  Logs all operations and errors, with automatic log size management.

- **qBittorrent Control**  
  Authenticates with qBittorrent’s Web API, updates preferences, and restarts the client if required.

- **ProtonVPN Detection**  
  Detects whether ProtonVPN is connected and extracts the active port from its log file.

- **Startup Option**  
  Allows enabling or disabling automatic startup with Windows.

---


## Configuration

The application uses an **INI file**, typically located in the user’s local application data folder.

- If the configuration file does not exist, a default one is created automatically on first run.
- Users may edit the INI file manually to customize behavior such as update intervals, qBittorrent credentials, and restart behavior.

---

## Usage

### Startup

- The application starts minimized and runs in the system tray.

### Configuration

- On first run, a default INI file is created if missing.
- Modify the configuration file to suit your preferences.

### Synchronization Loop

1. Checks whether ProtonVPN is connected.
2. Reads the VPN-assigned port from ProtonVPN logs.
3. Authenticates with qBittorrent and retrieves the current listening port.
4. If ports differ:
   - Updates qBittorrent’s port.
   - Restarts qBittorrent if configured.
5. Waits for the configured interval before repeating.

### Tray Menu Options

- Show logs
- Show configuration
- Enable/disable startup with Windows
- Exit application

---

## Logging

- All actions and errors are logged to `qbPortWeaver.log`.
- The log file is automatically deleted if it exceeds **5 MB** to prevent excessive disk usage.

---

## Error Handling

- If ProtonVPN is not connected or the port cannot be determined, the issue is logged and the update is skipped.
- If qBittorrent is not running or cannot be updated, errors are logged and the loop continues after the next interval.

---

## Extensibility

The modular architecture makes it easy to:

- Add support for other VPN providers
- Integrate additional torrent clients
- Extend configuration or logging features

---

## Example Workflow

1. User starts **qbPortWeaver**
2. Application loads configuration and log files
3. ProtonVPN status is checked and port retrieved
4. qBittorrent port is compared and updated if needed
5. qBittorrent restarts (if enabled)
6. Actions are logged
7. Application waits until the next interval

---

## Requirements

- Windows OS
- ProtonVPN installed and running
- qBittorrent installed with Web API enabled

---

## License

Free of use and distribution. No warranty provided.

## Author
Developed by eiKo Solutions

