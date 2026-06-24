<div align="center">

[🇨🇳 中文](README.md) | [🇺🇸 English](README.en.md)

</div>

---

# DynamicIsland 🏝️

A Dynamic Island for Windows — a floating capsule centered at the top of the screen, integrating system status, media controls, and instant alerts.

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4) ![WPF](https://img.shields.io/badge/WPF-Windows-0078D6) ![License](https://img.shields.io/badge/license-MIT-green)

---

## Features

### Display States

| State | Trigger | Effect |
|---|---|---|
| **Collapsed** | Default | 200×40 semi-transparent capsule showing the highest-priority info |
| **Hover** | Mouse enter | Slight scale-up + brighter border highlight |
| **Expanded** | Click | 460×160 combo dashboard: paged panels + system resource bar |
| **Alert** | Instant event | 420×40 capsule pop-in + heartbeat animation + shimmer sweep, auto-dismiss after seconds |

### Data Sources

| Source | Collapsed State | Expanded State |
|---|---|---|
| 🕒 **Clock** | Current time | Date + seconds |
| 🎵 **Media Control** | Song title / artist | Cover art + progress bar + playback controls |
| 🖥 **System Resources** | CPU/RAM usage (threshold-triggered) | CPU/RAM/GPU/VRAM/NET dashboard bars |
| 🔋 **Battery Change** | Instant alert | — |
| 📋 **Clipboard Copy** | Instant alert | — |
| 🔌 **USB Plug/Unplug** | Instant alert | — |
| 📡 **Bluetooth Connection** | Instant alert | — |
| 🌐 **Network Change** | Instant alert | — |
| ⬇ **Download Complete** | Instant alert (with "Open" button) | — |

### Highlights

- **Multi-monitor support** — Select target monitor in settings; auto-migrate on hot plug/unplug
- **Fullscreen suppression** — Auto-hides when a fullscreen app is running, never obstructs gameplay/video
- **Dark/Light theme** — Follows Windows theme automatically
- **Marquee text** — Long titles auto-scroll horizontally
- **Alert statistics** — Tracks each alert type's occurrence count, persisted across restarts
- **System notification** — Optionally pushes alerts to Windows Notification Center
- **Tray icon** — Backup entry point (Settings / Test / Exit)

---

## Screenshots

*(Coming soon)*

---

## Requirements

- Windows 10 (19041+) / Windows 11
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (development)
- SMTC-compatible players for audio session (Netease Cloud Music / Bilibili / Spotify, etc.)

## Quick Start

```bash
# Clone
git clone https://github.com/A-pei-lun/DynamicIsland.git
cd DynamicIsland

# Build
dotnet build

# Run
dotnet run --project DynamicIsland
```

After first launch, right-click the tray icon → "Settings" to configure target monitor, visibility toggles, thresholds, etc.

## Project Structure

```
DynamicIsland/
├── MainWindow.xaml(.cs)    # Main window: state machine + animation + interaction
├── IslandDashboard.xaml(.cs) # Expanded combo dashboard
├── MarqueeText.cs           # Marquee text control
├── TrayIcon.cs              # System tray
├── AutoStart.cs             # Startup management
├── DisplaySettings.cs       # Settings model (singleton)
├── MonitorInfo.cs           # Monitor information
├── FullScreenDetector.cs    # Fullscreen detection
│
├── Island/                  # Core abstractions
│   ├── IIslandSource.cs     # Data source interface
│   ├── IIslandPanel.cs      # Expanded panel interface
│   ├── IIslandAlert.cs      # Alert model
│   ├── IslandHost.cs        # Source arbitration
│   ├── AlertHost.cs         # Alert queue
│   ├── AlertStats.cs        # Alert statistics
│   └── SystemNotifier.cs    # Windows notification push
│
├── Sources/                 # Data source implementations
│   ├── ClockSource.cs
│   ├── MediaSource.cs
│   ├── SystemResourceSource.cs
│   ├── MediaPanel.cs
│   ├── NotificationPanel.cs
│   ├── StatsPanel.cs
│   └── ...
│
├── Alerts/                  # Alert source implementations
│   ├── BatteryAlertSource.cs
│   ├── ClipboardAlertSource.cs
│   ├── UsbAlertSource.cs
│   ├── BluetoothAlertSource.cs
│   ├── NetworkAlertSource.cs
│   ├── DownloadAlertSource.cs
│   ├── AlertView.xaml(.cs)
│   └── NotificationListView.xaml(.cs)
│
└── SettingsWindow.xaml(.cs) # Settings window
```

## Architecture

- **WPF** (.NET 10, Windows TFM `10.0.19041.0`)
- **Window layer**: `AllowsTransparency=True` borderless window, top-center aligned
- **Data flow**: `IIslandSource` → `IslandHost` (priority arbitration) → `MainWindow` (rendering)
- **Alert flow**: `AlertSource` → `AlertHost` (priority queue) → `MainWindow` (preemptive display)
- **Expanded state**: `IslandDashboard` paged container, scroll wheel navigation + bottom system resource bar
- **DPI adaptation**: Scales proportionally by screen height `[0.7, 2.0]`

## Build

```bash
dotnet build
dotnet publish -c Release -o publish
```

## Installer

```bash
# Prerequisite: Install Inno Setup 6 (https://jrsoftware.org/isdl.php)
# Or run the build script (auto-detects ISCC)
powershell -ExecutionPolicy Bypass -File build-release.ps1
```

Generates `publish/DynamicIsland-Setup-1.0.0.exe`. Double-click to install; uninstallable via Control Panel ("Add or Remove Programs") / Start Menu.

### Manual Installation (No Installer)

```bash
dotnet publish -c Release --self-contained -r win-x64 -o ./publish
```

Distribute the entire `publish/` directory to any path and run `DynamicIsland.exe`.

### Uninstall

- **Installer**: Control Panel → Add or Remove Programs → DynamicIsland → Uninstall
- **Manual**: Delete the directory entirely, no registry residue

## License

MIT
