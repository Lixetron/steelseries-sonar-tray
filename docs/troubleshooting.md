# Troubleshooting

[← Back to README](../README.md) · [User guide](user-guide.md)

---

## Status shows “Connecting to Sonar…” or “Sonar API unavailable”

1. Open **SteelSeries GG** and confirm **Sonar** is enabled.
2. Restart GG if Sonar was started after the tray app.
3. Check the status line for the API port when connected.
4. VPN or security tools can interfere with GG’s local HTTPS.

## Media keys still change Windows volume

- Enable **Media Keys Override** in Settings.
- Test with another keyboard — some proprietary drivers bypass standard media keys.
- Disable other global keyboard hooks temporarily.

## Mixer values drift or revert

Sonar is the source of truth. GG, games, or hotkeys may change volumes while the mixer is open; the app resyncs while visible.

## Volume overlay never appears

- Enable **Volume Overlay** in Settings.
- Overlay triggers only from **Media Keys Override** today, not slider changes.
- Hidden intentionally in fullscreen and presentation mode.

## Media / Aux channels missing

Enable the channel in Sonar **and** confirm the virtual device exists in Windows sound settings.

## Discord double audio / echo

Enable **Discord Screenshare Echo Fix** in Settings. See [Discord Screenshare Echo Fix](user-guide.md#discord-screenshare-echo-fix) for which endpoints are targeted in streamer vs classic mode.

Quick checks:

- **Streamer:** verify mic broadcast and self-monitoring icons; inspect **Sonar — Microphone** (Playback), **Sonar — Stream**, and physical output in `sndvol`.
- **Classic:** Discord is muted on **Sonar — Microphone** playback only — not capture.

## Tray or app icon looks stale after a rebuild

Close the app fully, then restart. If Explorer still shows the old icon: `taskkill /f /im explorer.exe` then `start explorer.exe`, or sign out and back in.

## After a SteelSeries GG update

GG updates can change the local API. If mixing breaks, file an issue with your GG and Sonar versions.
