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

## Localization

UI text lives in JSON language packs, not in XAML or code-behind. The catalog is flat
(`"section.key": "text"`), so adding a string never requires touching C#.

| Path | Purpose |
|------|---------|
| `Localization/Languages/en.json` | Default/fallback language (embedded at build time) |
| `Localization/Languages/ru.json` | Russian (embedded) |
| `Localization/LocalizationManager.cs` | Loads packs, resolves `auto`, raises change notifications |
| `Localization/Loc.cs` | Code-behind facade: `Loc.Get("key")`, `Loc.Format("key", arg)`, `Loc.Text("key")` |
| `Localization/TrExtension.cs` | XAML markup extension: `Text="{loc:Tr section.key}"` |

**Use it from XAML**

```xml
xmlns:loc="clr-namespace:SonarQuickMixer.Localization"
...
<TextBlock Text="{loc:Tr settings.features}"/>
<Button Content="{loc:Tr common.cancel}"/>
```

`Tr` returns a binding to the catalog indexer, so every visible label refreshes in place on a
language switch. Do not use it inside a `Style.Setter` (WPF does not support bindings there).

**Use it from C#**

```csharp
StatusText.Text = Loc.Get("mixer.status.connecting");
SettingsVersionText.Text = Loc.Format("settings.installed", AppVersion.Display);

// Store a token for text rendered later (status lines, view-model props):
private LocalizedText _status = Loc.Text("midi.status.ready");
public string StatusText => _status.ToString();
```

For code-generated combo boxes and status lines, subscribe to `Loc.LanguageChanged` and rebuild
the items / raise `PropertyChanged`. Existing examples: `SettingsPanelController.RefreshLocalization`,
`MidiConfigWindow.OnLanguageChanged`, `MidiConfigController.RefreshLocalization`.

**Add a language**

1. Copy `Localization/Languages/en.json` to `Localization/Languages/<code>.json`.
2. Set `code`, `nativeName` (shown in the language picker), and `culture`.
3. Translate every value. Keep `{0}`, `{1}`, … placeholders; the parity test fails if they differ.
4. Rebuild. The language appears automatically in **Settings → Appearance → Language**.

No registration list exists — packs are discovered as embedded resources.

**Drop-in translation without rebuilding**

Create a `Languages` folder next to `SonarQuickMixer.exe` and put `<code>.json` there, then restart
the app. Files with the same `code` as an embedded pack replace it entirely (handy for testing a
translation); new codes add a language. Malformed files are ignored so a bad translation cannot
prevent startup.

**Tests**

`steelseries-sonar-tray.tests/Localization/LocalizationTests.cs` verifies that all packs have the
same keys, no empty values, matching format placeholders, English fallback, language-switch events,
and external overrides. Run `dotnet test steelseries-sonar-tray.sln`.

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
