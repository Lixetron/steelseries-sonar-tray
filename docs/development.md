# Development

[← Back to README](../README.md) · [Architecture](architecture.md)

---

## Prerequisites

Windows 10+, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), PowerShell.

## Build and run

```powershell
dotnet build steelseries-sonar-tray.sln -c Release
dotnet run --project steelseries-sonar-tray/steelseries-sonar-tray.csproj
```

VS Code tasks (`.vscode/tasks.json`): `build: release`, `run`, `publish: dist`, `publish: single exe`.

## Publish

```powershell
.\scripts\publish.ps1 -Single   # dist-single\SonarQuickMixer.exe + Presets\ (self-contained)
.\scripts\publish.ps1           # dist\SonarQuickMixer.exe + Presets\ (needs .NET 8 runtime)
```

Release CI zips `dist-single\*` → `SonarQuickMixer-vX.Y.Z-single.zip` (exe keeps the plain name `SonarQuickMixer.exe`).

Profiles: `Folder` (framework-dependent) and `SingleFile` (`win-x64`, self-contained) in `Properties/PublishProfiles/`.

## Cutting a release

1. Move items from `## [Unreleased]` in [`CHANGELOG.md`](../CHANGELOG.md) into a new `## [X.Y.Z] - YYYY-MM-DD` section (user-facing notes: Added / Changed / Fixed). Update the compare links at the bottom of that file.
2. Bump `<Version>` in `steelseries-sonar-tray.csproj` to the same `X.Y.Z`.
3. Commit, then tag and push: `git tag vX.Y.Z && git push origin HEAD --tags`
4. [Release workflow](../.github/workflows/release.yml) builds both assets, extracts that changelog section into the GitHub Release body (plus Downloads), and publishes the zips.

The workflow **fails** if `CHANGELOG.md` has no matching `## [X.Y.Z]` section for the tag — write notes before tagging.

## Naming

| Context | Name |
|---------|------|
| Display name | Sonar Quick Mixer |
| Executable, namespace, AppData | `SonarQuickMixer` |
| Repository folder | `steelseries-sonar-tray` |

## Regenerating icons

After editing `scripts/GenerateIcons.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/GenerateIcons.ps1
dotnet build steelseries-sonar-tray/steelseries-sonar-tray.csproj -c Release
```

Commit script output (`*.ico`, `*.png`) with script changes.

---

## Roadmap

- [ ] Custom hotkeys per channel
- [x] Physical device support (MIDI multi-device / DryWetMidi)
- [ ] Stream Deck / HID companions
- [ ] Volume overlay on all volume changes (needs Sonar push/poll)

Completed: Discord echo fix, GitHub Releases, update notifications, MIDI Blueprint + Learn, layout presets, staged bindings / LED feedback.

For MIDI layout JSON and DIY presets, see [MIDI layout preset authoring](midi-preset-authoring.md).

---

## Limitations

Does **not** replace SteelSeries GG, route apps to channels, configure mic/EQ/spatial audio, or support macOS/Linux. The Sonar HTTP API is undocumented — best-effort compatibility.

---

## Contributing

Issues and PRs welcome. For bugs, include Windows version, GG/Sonar version, steps to reproduce, relevant `settings.json` excerpt, and whether streamer mode is on.

For code: focused diffs, `dotnet build steelseries-sonar-tray.sln -c Release` passes, run `GenerateIcons.ps1` if icons change, describe user-visible behavior in the PR.
