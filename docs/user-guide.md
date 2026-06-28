# User guide

[← Back to README](../README.md) · [Troubleshooting](troubleshooting.md) · [Architecture](architecture.md)

---

## Features

### Quick Mixer (tray popup)

- **Master**, **Game**, **Chat**, **Media**, and **Aux** channels
- Per-channel **mute** and **volume** sliders
- **Streamer mode** support: separate **Monitor** and **Stream** mixes, plus per-channel stream routing toggles
- Anchors near the tray icon; closes when focus leaves the window
- Smooth inertial scrolling in mixer and settings lists
- Status line shows connection state, streamer mode, enabled channels, and API port
- Mixer state syncs from Sonar while the window is open

### Tray icon

| Style | When to use |
|-------|-------------|
| **Auto** *(default)* | Dark Windows theme → cyan accent; light theme → dark bars |
| **Accent** | Always cyan (`#60CDFF`), matches the app accent |
| **White** | Neutral bars on dark taskbars |
| **Dark** | Dark bars on light taskbars |

The executable uses a separate **app icon** (rounded tile) for Task Manager and shortcuts. Tray icons are minimal three-bar glyphs without a background tile.

### Media Keys Override

When enabled, global **Volume Up**, **Volume Down**, and **Volume Mute** keys are intercepted and applied to a **selected Sonar channel** (default: Master) via the Sonar API.

| Key | Action |
|-----|--------|
| Volume Up | +2% on target channel (monitoring mix) |
| Volume Down | −2% on target channel |
| Volume Mute | Toggle mute on target channel |

> **Tip:** Disable duplicate volume hotkeys in **SteelSeries GG → Sonar → Settings → Hotkeys**. This app only intercepts standard media keys, not Sonar’s custom bindings.

### Volume Overlay

A small top-center HUD after media-key adjustments (when enabled): channel name, mute icon, percentage, and level bar. Hidden in fullscreen Direct3D, presentation mode, or when another window covers the monitor.

### Audio Visualizer

Optional live level meters on mixer sliders via [NAudio](https://github.com/naudio/NAudio) WASAPI peak values on Sonar virtual render devices.

### Discord Screenshare Echo Fix

Mutes **only Discord** (`Discord`, `DiscordPTB`, `DiscordCanary`) via Windows per-app volume on specific endpoints — not through the Sonar channel API. Original mute state is restored when the option is off or routing changes. Polls every **2 seconds** via `GET /mode` and `GET /streamRedirections`.

| Sonar mode | Endpoint | When targeted |
|------------|----------|---------------|
| **Streamer** | **Sonar — Microphone** (playback / render, not capture) | Always watched |
| **Streamer** | **Sonar — Stream** | Mic **broadcast** to stream mix is on |
| **Streamer** | **Physical monitoring output** (from `monitoring.deviceId`) | **Self-monitoring** is on |
| **Classic** | **Sonar — Microphone** (playback / render, not capture) | Always watched |

Mute applies only when Discord has an audio session on that endpoint. **Never muted:** Sonar Microphone **capture**, Game/Chat/Media/Aux, other apps.

### Update check

On startup, checks GitHub [Releases](https://github.com/lixetron/steelseries-sonar-tray/releases) in the background. A newer version shows a cyan dot on **Settings** and an update card in **Settings → About**. Fails silently when offline.

---

## Requirements

| Requirement | Notes |
|-------------|-------|
| **Windows 10 or later** | 64-bit (`win-x64`) |
| **SteelSeries GG** with **Sonar** running | Sonar must start before the tray app can connect |
| **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** | Only for the folder `.zip` publish; single-file bundles the runtime |

---

## Installation

Open [**Releases**](https://github.com/lixetron/steelseries-sonar-tray/releases):

| Asset | Best for |
|-------|----------|
| **`SonarQuickMixer-vX.Y.Z-single.exe`** | Most users — self-contained, no separate .NET install |
| **`SonarQuickMixer-vX.Y.Z-win-x64.zip`** | Smaller download; requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Run `SonarQuickMixer.exe`. Left-click the tray icon to open the mixer.

To build from source or publish locally, see [Development](development.md).

### Autostart (optional)

Enable **Start with Windows** in **Settings** (registers in `HKCU\...\Run`). Ensure SteelSeries GG also starts with Windows.

<details>
<summary>Manual startup folder (alternative)</summary>

Place a shortcut in the Startup folder (`Win+R` → `shell:startup`). Prefer the in-app toggle for a clean uninstall path.
</details>

---

## Usage

| Action | Result |
|--------|--------|
| **Left-click** tray icon | Open mixer near the cursor |
| **Right-click** → **Open Mixer** / **Exit** | Open mixer or quit |
| **Launch again** while running | Focus existing instance (single-instance app) |

**Settings** (gear icon): toggle features listed in [Settings reference](#settings-reference). **Back** or click outside to close.

- Drag sliders or click the track to jump (large jumps apply immediately; small drags are throttled).
- Mute buttons follow the monitoring or streaming path for each row.
- In streamer mode, **Stream** rows mirror Sonar’s streamer mixer.

Settings save on toggle or when the mixer closes. Tray icon style applies immediately without restart.

---

## Settings reference

```text
%LocalAppData%\Lixetron\SonarQuickMixer\settings.json
```

| Property | Default | Description |
|----------|---------|-------------|
| `RunAtWindowsStartup` | `false` | Autostart via `HKCU\...\Run` |
| `MediaKeysOverride` | `false` | Intercept Volume Up/Down/Mute globally |
| `MediaKeysOverrideChannel` | `"master"` | `master`, `game`, `chatRender`, `media`, `aux` |
| `VolumeOverlayEnabled` | `true` | HUD after media-key volume changes |
| `DiscordScreenshareEchoFix` | `false` | Per-app Discord mute on Sonar endpoints (see above) |
| `AudioVisualizerEnabled` | `true` | Live level meters on sliders |
| `TrayIconStyle` | `0` | `0` Auto, `1` Accent, `2` White, `3` Dark |

You can edit the file while running; reopen settings or restart for all services to pick up changes.
