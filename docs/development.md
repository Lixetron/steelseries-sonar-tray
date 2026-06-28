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
.\scripts\publish.ps1 -Single   # dist-single\SonarQuickMixer.exe (self-contained)
.\scripts\publish.ps1           # dist\SonarQuickMixer.exe (needs .NET 8 runtime)
```

Profiles: `Folder` (framework-dependent) and `SingleFile` (`win-x64`, self-contained) in `Properties/PublishProfiles/`.

## Cutting a release

1. Bump `<Version>` in `steelseries-sonar-tray.csproj`.
2. Tag and push: `git tag v1.0.0 && git push origin master --tags`
3. [Release workflow](../.github/workflows/release.yml) builds both assets and publishes to GitHub Releases.

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
- [ ] Physical device support (Stream Deck, MIDI/HID)
- [ ] Volume overlay on all volume changes (needs Sonar push/poll)

Completed: Discord echo fix, GitHub Releases, update notifications.

---

## Limitations

Does **not** replace SteelSeries GG, route apps to channels, configure mic/EQ/spatial audio, or support macOS/Linux. The Sonar HTTP API is undocumented — best-effort compatibility.

---

## Contributing

Issues and PRs welcome. For bugs, include Windows version, GG/Sonar version, steps to reproduce, relevant `settings.json` excerpt, and whether streamer mode is on.

For code: focused diffs, `dotnet build steelseries-sonar-tray.sln -c Release` passes, run `GenerateIcons.ps1` if icons change, describe user-visible behavior in the PR.
