using System.IO;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Layout-constructor mode entry/exit, and layout preset use/delete/save-as/rename/export/import.
/// Split out of MidiConfigController.cs purely to keep that file a manageable size — same
/// partial class, same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigController
{
    public void EnterLayoutConstructor()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return;
        }

        if (!ConfirmDiscardBindingDraftsIfNeeded("Opening the layout constructor will discard unsaved channel assignments."))
        {
            return;
        }

        CancelLearn();
        _draftLayout = PresetCatalog.CloneLayout(_layout);
        EnsureUniqueDraftCells(_draftLayout);

        _controlIdsAtConstructorEnter = _draftLayout.Controls
            .Select(c => c.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DraftControlLabel = string.Empty;
        DraftRowSpan = 1;
        DraftColSpan = 1;
        DraftHideBorder = false;
        DraftKeepSpacing = false;
        DraftContentJustify = MidiContentJustify.Pack;
        DraftContentAlign = MidiContentJustify.Pack;
        SetDraftHideBorderEnabled(false);
        IsLayoutConstructorMode = true;
        RebuildConstructorView();
        StatusText =
            "Layout constructor — drag areas/controls anywhere; Learn maps hardware here.";
    }

    public void CancelLayoutConstructor(bool silent = false)
    {
        if (!IsLayoutConstructorMode)
        {
            return;
        }

        _draftLayout = null;
        _controlIdsAtConstructorEnter = null;
        IsLayoutConstructorMode = false;
        ReloadLayoutForSelectedDevice();
        if (!silent)
        {
            StatusText = "Layout edits discarded.";
        }
    }

    public bool SaveLayoutConstructor()
    {
        if (!IsLayoutConstructorMode || _draftLayout is null || string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            return false;
        }

        var deviceName = SelectedDeviceName;
        var core = MidiDevicePortNaming.CoreProductName(deviceName);
        _draftLayout.DeviceMatch =
        [
            deviceName,
            ..(string.Equals(core, deviceName, StringComparison.OrdinalIgnoreCase) ? Array.Empty<string>() : [core])
        ];

        if (string.IsNullOrWhiteSpace(_draftLayout.Name) || _draftLayout.Name == "Generic Custom Grid")
        {
            _draftLayout.Name = $"{core} (custom)";
        }

        try
        {
            var activeKey = _presets.GetActivePresetKey(deviceName);
            var targetFile = MidiPresetSelectionStore.UserFileNameFromKey(activeKey);
            string path;
            if (!string.IsNullOrWhiteSpace(targetFile))
            {
                path = _presets.SaveUserLayout(_draftLayout, targetFileName: targetFile, createNewFile: false);
            }
            else
            {
                // Editing official / generic → create a new user preset.
                path = _presets.SaveUserLayout(_draftLayout, createNewFile: true);
                _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
            }
        }
        catch
        {
            StatusText = "Failed to save user layout.";
            return false;
        }

        CleanupOrphanBindingsAfterLayoutSave(deviceName, _draftLayout);
        _layout = PresetCatalog.CloneLayout(_draftLayout);
        _draftLayout = null;
        _controlIdsAtConstructorEnter = null;
        IsLayoutConstructorMode = false;
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        StatusText = $"Saved custom layout for {deviceName}.";
        _midi.ClearStagedControlFeedback(deviceName);
        _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        return true;
    }

    /// <summary>Switch to the official (or built-in) preset without deleting user presets.</summary>
    public bool UseOfficialLayoutPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a device first.";
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            StatusText = "Finish or cancel layout editing first.";
            return false;
        }

        CancelLearn();
        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        StatusText = "Using official / built-in layout preset.";
        return true;
    }

    /// <summary>Deletes the currently selected user preset file.</summary>
    public bool DeleteSelectedUserPreset()
    {
        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName }
            || string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            StatusText = "Select a user layout preset to delete.";
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            StatusText = "Finish or cancel layout editing first.";
            return false;
        }

        CancelLearn();
        if (!_presets.DeleteUserPresetFile(fileName))
        {
            StatusText = $"Could not delete preset “{fileName}”.";
            return false;
        }

        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        StatusText = $"Deleted user preset “{fileName}”. Switched to official layout.";
        return true;
    }

    /// <summary>
    /// Saves the current layout (constructor draft or active) as a new named user preset and selects it.
    /// </summary>
    public bool TrySaveLayoutPresetAs(string presetName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device first.";
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a preset name.";
            return false;
        }

        var deviceName = SelectedDeviceName;
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);

        layout.Name = name;
        EnsureDeviceMatchIncludes(layout, deviceName);

        try
        {
            var path = _presets.SaveUserLayout(layout, createNewFile: true);
            _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));

            if (IsLayoutConstructorMode && _draftLayout is not null)
            {
                CleanupOrphanBindingsAfterLayoutSave(deviceName, _draftLayout);
                _layout = PresetCatalog.CloneLayout(_draftLayout);
                _draftLayout = null;
                _controlIdsAtConstructorEnter = null;
                IsLayoutConstructorMode = false;
            }

            OnPropertyChanged(nameof(HasUserLayoutOverride));
            ReloadLayoutForSelectedDevice();
            StatusText = $"Saved preset “{name}”.";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusText = "Failed to save named preset.";
            return false;
        }
    }

    /// <summary>
    /// Renames the active user preset in place (updates <c>name</c> in the same JSON file).
    /// Official / built-in presets cannot be renamed — use Save as… first.
    /// </summary>
    public bool TryRenameCurrentLayoutPreset(string presetName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device first.";
            return false;
        }

        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName })
        {
            error = "Select a user layout preset to rename. Official presets are read-only — use Save as… first.";
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a preset name.";
            return false;
        }

        if (string.Equals(LayoutName, name, StringComparison.Ordinal))
        {
            StatusText = "Preset name unchanged.";
            return true;
        }

        var deviceName = SelectedDeviceName;
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);

        layout.Name = name;
        EnsureDeviceMatchIncludes(layout, deviceName);

        try
        {
            var path = _presets.SaveUserLayout(layout, targetFileName: fileName, createNewFile: false);
            var key = MidiPresetSelectionStore.UserKey(Path.GetFileName(path));
            _presets.SetActivePresetKey(deviceName, key);

            if (IsLayoutConstructorMode && _draftLayout is not null)
            {
                _draftLayout.Name = name;
            }

            _layout.Name = name;
            OnPropertyChanged(nameof(LayoutName));
            RefreshLayoutPresets(key);
            StatusText = $"Renamed preset to “{name}”.";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusText = "Failed to rename preset.";
            return false;
        }
    }

    /// <summary>Current layout JSON (constructor draft if editing, otherwise resolved layout).</summary>
    public string ExportLayoutJson()
    {
        var layout = IsLayoutConstructorMode && _draftLayout is not null
            ? PresetCatalog.CloneLayout(_draftLayout)
            : PresetCatalog.CloneLayout(_layout);
        return MidiLayoutJson.Serialize(layout);
    }

    /// <summary>
    /// Suggested file stem for SaveFileDialog (layout name or device).
    /// </summary>
    public string GetExportFileStem()
    {
        var layout = IsLayoutConstructorMode && _draftLayout is not null ? _draftLayout : _layout;
        var seed = layout.DeviceMatch.FirstOrDefault()
                   ?? SelectedDeviceName
                   ?? layout.Name
                   ?? "midi-layout";
        var safe = System.Text.RegularExpressions.Regex.Replace(seed.Trim(), @"[^\w\-]+", "-");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"-+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "midi-layout" : safe.ToLowerInvariant();
    }

    /// <summary>
    /// Imports a layout JSON. In constructor mode replaces the draft; otherwise saves as a new user preset.
    /// </summary>
    public bool TryImportLayoutJson(string json, out string error)
    {
        error = string.Empty;
        if (!MidiLayoutJson.TryParse(json, out var parsed, out error) || parsed is null)
        {
            return false;
        }

        var layout = PresetCatalog.CloneLayout(parsed);

        if (IsLayoutConstructorMode)
        {
            if (_draftLayout is null)
            {
                error = "Layout constructor is not ready.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
            {
                EnsureDeviceMatchIncludes(layout, SelectedDeviceName);
            }

            _draftLayout = layout;
            RebuildConstructorView();
            StatusText = $"Imported layout “{layout.Name}” into the editor (not saved yet).";
            return true;
        }

        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = "Select a MIDI device before importing a layout.";
            return false;
        }

        var deviceName = SelectedDeviceName;
        EnsureDeviceMatchIncludes(layout, deviceName);
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name == "Custom")
        {
            layout.Name = $"{MidiDevicePortNaming.CoreProductName(deviceName)} (imported)";
        }

        try
        {
            var path = _presets.SaveUserLayout(layout, createNewFile: true);
            _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            error = $"Failed to save imported layout: {ex.Message}";
            return false;
        }

        CancelLearn();
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        StatusText = $"Imported layout “{layout.Name}” as a new user preset.";
        return true;
    }

    private static void EnsureDeviceMatchIncludes(MidiDeviceLayout layout, string deviceName)
    {
        layout.DeviceMatch ??= [];
        var core = MidiDevicePortNaming.CoreProductName(deviceName);
        if (!layout.DeviceMatch.Any(m => string.Equals(m, deviceName, StringComparison.OrdinalIgnoreCase)))
        {
            layout.DeviceMatch.Insert(0, deviceName);
        }

        if (!string.Equals(core, deviceName, StringComparison.OrdinalIgnoreCase)
            && !layout.DeviceMatch.Any(m => string.Equals(m, core, StringComparison.OrdinalIgnoreCase)))
        {
            layout.DeviceMatch.Add(core);
        }
    }
}
