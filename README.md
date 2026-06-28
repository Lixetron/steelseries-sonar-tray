<p align="center">
  <img src="steelseries-sonar-tray/Assets/app-icon.png" alt="Sonar Quick Mixer app icon" width="128" height="128">
</p>

# Sonar Quick Mixer

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-blue)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

A lightweight Windows system-tray companion for [SteelSeries Sonar](https://www.steelseries.com/gg/sonar). Open a fast mixer popup, adjust channel volumes without launching SteelSeries GG, redirect hardware media keys to Sonar, and get a clean on-screen volume indicator.

**This project is not affiliated with, endorsed by, or supported by SteelSeries.** It talks to the local Sonar HTTP API exposed by SteelSeries GG while Sonar is running.

---

## Why this exists

| Pain point | What this app does |
|------------|-------------------|
| GG is heavy for a quick volume tweak | Tray popup mixer opens in one click |
| Windows media keys fight Sonar as the default audio device | **Media Keys Override** sends Volume Up/Down/Mute to a Sonar channel |
| No clear feedback when Sonar handles volume | **Volume Overlay** shows channel name, level, and mute state |
| Hard to see which channel is active while mixing | **Audio Visualizer** paints live levels on sliders |
| Discord double audio with Sonar routing | **Discord Screenshare Echo Fix** — per-app mute on Sonar endpoints |

Sonar Quick Mixer is a **daily-driver layer on top of Sonar**, not a replacement for GG. Routing apps to channels, mic setup, spatial audio, and driver management still live in SteelSeries GG.

---

## Quick start

1. Install **SteelSeries GG** with **Sonar** running.
2. Download **`SonarQuickMixer-vX.Y.Z-single.exe`** from [**Releases**](https://github.com/lixetron/steelseries-sonar-tray/releases).
3. Run it — a tray icon appears. **Left-click** to open the mixer.

Requires **Windows 10+** (64-bit). The single-file build bundles .NET 8; the folder `.zip` needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

Full installation options, usage, and settings: [User guide](docs/user-guide.md)

---

## Documentation

| Document | Contents |
|----------|----------|
| [User guide](docs/user-guide.md) | Features, installation, usage, settings |
| [Troubleshooting](docs/troubleshooting.md) | Common problems and fixes |
| [Architecture](docs/architecture.md) | How the app talks to Sonar and Windows audio |
| [Development](docs/development.md) | Build, release, contributing, roadmap |

---

## License

[MIT](LICENSE) — Copyright © 2026 Lixetron.

**SteelSeries**, **SteelSeries GG**, and **Sonar** are trademarks of SteelSeries ApS. This project is an independent community utility.
