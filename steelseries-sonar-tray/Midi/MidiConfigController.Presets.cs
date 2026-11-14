using System.IO;
using SonarQuickMixer.Localization;

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
            SetStatus("midi.status.selectDeviceFirst");
            return;
        }

        if (!ConfirmDiscardBindingDraftsIfNeeded(Loc.Get("midi.confirm.enterConstructor")))
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
        OnPropertyChanged(nameof(LayoutDisplayName));
        SetStatus("midi.status.layoutConstructor");
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
            SetStatus("midi.status.layoutEditsDiscarded");
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

        if (string.IsNullOrWhiteSpace(_draftLayout.Name) || _draftLayout.Name == PresetCatalog.GenericLayoutName)
        {
            _draftLayout.Name = Loc.Format("midi.customName", core);
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
            SetStatus("midi.status.failedSaveUserLayout");
            return false;
        }

        CleanupOrphanBindingsAfterLayoutSave(deviceName, _draftLayout);
        _layout = PresetCatalog.CloneLayout(_draftLayout);
        _draftLayout = null;
        _controlIdsAtConstructorEnter = null;
        IsLayoutConstructorMode = false;
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        SetStatus(Loc.Text("midi.status.savedCustomLayout", deviceName));
        _midi.ClearStagedControlFeedback(deviceName);
        _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        return true;
    }

    /// <summary>Switch to the official (or built-in) preset without deleting user presets.</summary>
    public bool UseOfficialLayoutPreset()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectDeviceFirst");
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            SetStatus("midi.status.finishOrCancelFirst");
            return false;
        }

        CancelLearn();
        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        SetStatus("midi.status.usingOfficialPreset");
        return true;
    }

    /// <summary>Deletes the currently selected user preset file.</summary>
    public bool DeleteSelectedUserPreset()
    {
        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName }
            || string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectUserPresetToDelete");
            return false;
        }

        if (IsLayoutConstructorMode)
        {
            SetStatus("midi.status.finishOrCancelFirst");
            return false;
        }

        CancelLearn();
        if (!_presets.DeleteUserPresetFile(fileName))
        {
            SetStatus(Loc.Text("midi.status.presetDeleteFailed", fileName));
            return false;
        }

        _presets.SetActivePresetKey(SelectedDeviceName, MidiPresetSelectionStore.OfficialKey);
        ReloadLayoutForSelectedDevice();
        SetStatus(Loc.Text("midi.status.presetDeleted", fileName));
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
            error = Loc.Get("midi.error.selectMidiDevice");
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = Loc.Get("midi.error.enterPresetName");
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
            SetStatus(Loc.Text("midi.status.presetSaved", name));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            SetStatus("midi.status.presetSaveFailed");
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
            error = Loc.Get("midi.error.selectMidiDevice");
            return false;
        }

        if (SelectedLayoutPreset is not { IsUser: true, FileName: { } fileName })
        {
            error = Loc.Get("midi.error.renameOfficial");
            return false;
        }

        var name = presetName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = Loc.Get("midi.error.enterPresetName");
            return false;
        }

        if (string.Equals(LayoutName, name, StringComparison.Ordinal))
        {
            SetStatus("midi.status.presetNameUnchanged");
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
            OnPropertyChanged(nameof(LayoutDisplayName));
            RefreshLayoutPresets(key);
            SetStatus(Loc.Text("midi.status.presetRenamed", name));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            SetStatus("midi.status.presetRenameFailed");
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
                error = Loc.Get("midi.error.constructorNotReady");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
            {
                EnsureDeviceMatchIncludes(layout, SelectedDeviceName);
            }

            _draftLayout = layout;
            RebuildConstructorView();
            OnPropertyChanged(nameof(LayoutDisplayName));
            SetStatus(Loc.Text("midi.status.layoutImportedIntoEditor", layout.Name));
            return true;
        }

        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            error = Loc.Get("midi.error.selectDeviceBeforeImport");
            return false;
        }

        var deviceName = SelectedDeviceName;
        EnsureDeviceMatchIncludes(layout, deviceName);
        if (string.IsNullOrWhiteSpace(layout.Name) || layout.Name == "Custom")
        {
            layout.Name = Loc.Format("midi.importedName", MidiDevicePortNaming.CoreProductName(deviceName));
        }

        try
        {
            var path = _presets.SaveUserLayout(layout, createNewFile: true);
            _presets.SetActivePresetKey(deviceName, MidiPresetSelectionStore.UserKey(Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            error = Loc.Format("midi.error.saveImportedFailed", ex.Message);
            return false;
        }

        CancelLearn();
        OnPropertyChanged(nameof(HasUserLayoutOverride));
        ReloadLayoutForSelectedDevice();
        SetStatus(Loc.Text("midi.status.layoutImported", layout.Name));
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
