# Changelog

All notable changes to **Sonar Quick Mixer** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

How to read this: each release lists **user-visible** changes. Download builds from
[GitHub Releases](https://github.com/lixetron/steelseries-sonar-tray/releases).

## [Unreleased]

## [1.2.0] - 2026-08-27

### Added

- Headset battery status in the mixer (optional), with clear charge / charging icons
- Choose Sonar **output** and **microphone** devices from the mixer (optional selectors)
- Tray icon badge when a newer version is available (same cue as the settings gear)

### Fixed

- Smoother MIDI volume sync with SteelSeries GG / Sonar 118+: fewer jumps, less fighting between the hardware fader and the on-screen mixer

## [1.1.0] - 2026-08-10

### Added

- **MIDI Setup**: map controllers to Sonar channels with layout presets, staged bindings, and optional LED feedback
- Official MIDI layout presets shipped next to the app in release zips

### Changed

- Recommended download is now `SonarQuickMixer-*-single.zip` (self-contained exe + `Presets\`)

### Fixed

- Single-file releases correctly include the `Presets\` folder

## [1.0.2] - 2026-07-04

### Fixed

- Game channel audio visualizer visibility
- Initial mixer window state on open

### Changed

- Clearer docs and README; internal code layout cleanup (no change to day-to-day usage)

## [1.0.1] - 2026-06-28

### Added

- In-app check for updates from GitHub Releases

### Changed

- Settings panel scrolls cleanly; less “jump” when opening settings
- Discord Screenshare Echo Fix is **off** by default (enable it only if you need it)

## [1.0.0] - 2026-06-27

### Added

- First public release: tray mixer, media keys override, volume overlay, audio visualizer, Discord screenshare echo fix, Start with Windows
- Automated GitHub Releases with downloadable builds

[Unreleased]: https://github.com/lixetron/steelseries-sonar-tray/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/lixetron/steelseries-sonar-tray/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/lixetron/steelseries-sonar-tray/compare/v1.0.2...v1.1.0
[1.0.2]: https://github.com/lixetron/steelseries-sonar-tray/compare/v1.0.1...v1.0.2
[1.0.1]: https://github.com/lixetron/steelseries-sonar-tray/compare/v1.0.0...v1.0.1
[1.0.0]: https://github.com/lixetron/steelseries-sonar-tray/releases/tag/v1.0.0
