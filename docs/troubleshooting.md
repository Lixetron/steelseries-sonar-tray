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

## MIDI device not listed / blueprint stays generic

1. Enable **MIDI** in Settings and click **Open MIDI Setup…**.
2. Confirm Windows sees the port (Device Manager / MIDI apps). Unplug/replug if needed.
3. Official layouts match on substrings in `deviceMatch` (e.g. `SMC-Mixer`). Custom JSON must match the real port name — see [MIDI preset authoring](midi-preset-authoring.md).
4. For M-VAVE SMC-Mixer, switch the hardware to **DAW Mode / Mode A** (`Shift+←`), not CC / Mode B.

## MIDI moves nothing in Sonar

- Select the device → **Use device**.
- Assign a Sonar **Channel** and an **Action** (`Volume` or `Mute toggle`), then **Save changes** (edits are staged until Save).
- Controls without factory hardware need **Learn** (layout constructor) or a baked `controller` in the preset.
- Confirm Sonar is connected (mixer status line).

## MIDI LED / lamp does not light

- LED feedback is optional and usually **not** baked into official presets — set **Source** / **Style** in MIDI Setup, then **Save changes**.
- Hardware lamps update **after Save**, not while editing.
- Pitch Bend fader “match” LEDs use soft-takeover: they blink only while Sonar level ≠ physical fader. Move the fader to match, or set Source to **Off**.
- Pads need an assigned Sonar channel for **On mute** / **On channel selected**.

## Unsaved MIDI edits / yellow outlines

Yellow borders and `*` next to Channel / Mode / Action / LED fields mean staged changes. Use **Save changes** or **Discard**. Switching preset or closing MIDI Setup may ask to discard unsaved assignments.

## Tray or app icon looks stale after a rebuild

Close the app fully, then restart. If Explorer still shows the old icon: `taskkill /f /im explorer.exe` then `start explorer.exe`, or sign out and back in.

## After a SteelSeries GG update

GG updates can change the local API. If mixing breaks, file an issue with your GG and Sonar versions.
