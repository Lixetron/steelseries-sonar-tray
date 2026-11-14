using SonarQuickMixer.Localization;
using SonarQuickMixer.Sonar;

namespace SonarQuickMixer.Midi;

/// <summary>
/// Staging, saving, discarding and clearing channel/mode/action binding assignments (the
/// yellow-chrome "unsaved" workflow). Split out of MidiConfigController.cs purely to keep that
/// file a manageable size — same partial class, same fields/state, no behavior change.
/// </summary>
public partial class MidiConfigController
{
    /// <summary>
    /// Stages channel / mode / action on the blueprint without writing midi-mappings.json.
    /// Call <see cref="SaveBindingDraftsAsync"/> to persist all staged assignments.
    /// </summary>
    public void StageBindingAssignment(
        BlueprintControlVm control,
        string? channelIdOrDisplayName,
        MidiValueMode mode,
        MidiBindingAction action)
    {
        if (!CanEditBindings || control.IsPlaceholder)
        {
            return;
        }

        var channelId = ResolveChannelId(channelIdOrDisplayName);
        action = MidiBindingActions.Normalize(control.Type, action);
        ApplyAssignmentToVm(control, channelId, mode, action);

        var persisted = !string.IsNullOrWhiteSpace(SelectedDeviceName)
            ? FindBindingForControl(SelectedDeviceName, control.Id)
            : null;

        var matchesPersisted = persisted is not null
                               && string.Equals(NormalizeChannelCompare(persisted.ChannelId), NormalizeChannelCompare(channelId), StringComparison.OrdinalIgnoreCase)
                               && persisted.Mode == mode
                               && MidiBindingActions.Normalize(control.Type, persisted.Action) == action
                               && control.Controller is int cc
                               && persisted.Controller == cc
                               && persisted.IsNote == control.IsNote
                               && persisted.IsPitchBend == control.IsPitchBend;

        if (matchesPersisted
            || (persisted is null
                && control.Controller is null
                && string.IsNullOrWhiteSpace(channelId)
                && mode == MidiValueMode.Absolute
                && action == MidiBindingAction.None))
        {
            _bindingDrafts.Remove(control.Id);
            if (persisted is not null)
            {
                ApplyBindingToVm(control, persisted);
            }
        }
        else
        {
            _bindingDrafts[control.Id] = new BindingAssignmentDraft(control.Id, channelId, mode, action);
        }

        SetBindingDraftDirty(_bindingDrafts.Count > 0);
        RefreshControlUnsavedIndicators(control.Id);
        SetStatus(_hasUnsavedBindingDrafts || _hasUnsavedLayoutChanges
            ? Loc.Text("midi.status.staged", control.Label)
            : Loc.Text("midi.status.matchesSaved", control.Label));
    }

    public async Task<bool> SaveBindingDraftsAsync()
    {
        if (!CanEditBindings)
        {
            SetStatus("midi.status.enableDeviceToSave");
            return false;
        }

        if (_bindingDrafts.Count == 0 && !_hasUnsavedLayoutChanges)
        {
            SetStatus("midi.status.nothingToSave");
            return true;
        }

        var deviceName = SelectedDeviceName;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            SetStatus("midi.status.selectDeviceFirst");
            return false;
        }

        var saved = 0;
        var skipped = 0;
        foreach (var draft in _bindingDrafts.Values.ToList())
        {
            var control = Controls.FirstOrDefault(c =>
                string.Equals(c.Id, draft.ControlId, StringComparison.OrdinalIgnoreCase));
            if (control is null || control.Controller is null)
            {
                skipped++;
                continue;
            }

            var storageDevice = ResolveStorageDeviceName(
                control.Controller.Value,
                control.IsNote,
                control.IsPitchBend,
                control.Id) ?? deviceName;

            var binding = new MidiBinding
            {
                DeviceName = storageDevice,
                Controller = control.Controller.Value,
                IsNote = control.IsNote,
                IsPitchBend = control.IsPitchBend,
                ChannelId = draft.ChannelId,
                Path = SonarMixerPath.Monitoring,
                Mode = draft.Mode,
                Action = MidiBindingActions.Normalize(control.Type, draft.Action),
                ControlId = control.Id,
                IsMotorized = false
            };

            _midi.MappingStore.RemoveMatching(b =>
                b.Controller == binding.Controller
                && b.IsNote == binding.IsNote
                && b.IsPitchBend == binding.IsPitchBend
                && DevicesShareProduct(b.DeviceName, binding.DeviceName)
                && !string.Equals(b.DeviceName, binding.DeviceName, StringComparison.OrdinalIgnoreCase));

            if (MidiConflictValidator.RequiresConflictConfirmation(_midi.MappingStore, binding, out var conflicts))
            {
                var confirmed = await ConfirmConflictAsync(conflicts).ConfigureAwait(true);
                if (!confirmed)
                {
                    SetStatus("midi.status.saveCancelledConflict");
                    return false;
                }
            }

            _midi.MappingStore.Upsert(binding);
            ApplyBindingToVm(control, binding);
            saved++;
        }

        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);

        var layoutSaved = false;
        if (_hasUnsavedLayoutChanges)
        {
            if (!PersistActiveLayoutToUserPreset())
            {
                return false;
            }

            SetLayoutDraftDirty(false);
            layoutSaved = true;
        }

        CapturePersistedFeedbackSnapshot();
        RefreshControlUnsavedIndicators();

        if (skipped > 0 && saved == 0 && !layoutSaved)
        {
            SetStatus("midi.status.saveNeedsHardware");
            return false;
        }

        SetStatus((saved, skipped, layoutSaved) switch
        {
            (0, _, true) => Loc.Text("midi.status.savedLayoutOnly"),
            (_, > 0, true) => Loc.Text("midi.status.savedWithLayout", saved, skipped),
            (_, > 0, false) => Loc.Text("midi.status.savedSkipped", saved, skipped),
            (_, _, true) => Loc.Text("midi.status.savedWithLayoutPreset", saved),
            _ => Loc.Text("midi.status.saved", saved)
        });
        _midi.ClearStagedControlFeedback(deviceName);
        _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        return true;
    }

    public void DiscardBindingDrafts(bool reloadLayout = true)
    {
        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);
        SetLayoutDraftDirty(false);
        if (!string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            _midi.ClearStagedControlFeedback(SelectedDeviceName);
        }

        if (reloadLayout)
        {
            ReloadLayoutForSelectedDevice();
            SetStatus("midi.status.discarded");
            _ = _midi.RefreshHardwareFeedbackAsync(force: true);
        }
    }

    public bool ConfirmDiscardBindingDraftsIfNeeded(string message)
    {
        if (!_hasUnsavedBindingDrafts && !_hasUnsavedLayoutChanges)
        {
            return true;
        }

        var proceed = ConfirmDiscardUnsavedAssignments?.Invoke(message) ?? true;
        if (!proceed)
        {
            return false;
        }

        DiscardBindingDrafts(reloadLayout: false);
        return true;
    }

    /// <summary>Removes hardware discovery and Sonar assignment for this blueprint slot (persisted immediately).</summary>
    public void ClearBinding(BlueprintControlVm control)
    {
        if (control.IsPlaceholder)
        {
            return;
        }

        _bindingDrafts.Remove(control.Id);
        SetBindingDraftDirty(_bindingDrafts.Count > 0);

        _midi.MappingStore.RemoveMatching(b =>
            string.Equals(b.ControlId, control.Id, StringComparison.OrdinalIgnoreCase)
            || (control.Controller is int cc
                && b.Controller == cc
                && b.IsNote == control.IsNote
                && b.IsPitchBend == control.IsPitchBend
                && (string.IsNullOrWhiteSpace(SelectedDeviceName)
                    || DevicesShareProduct(b.DeviceName, SelectedDeviceName))));

        ResetControlVm(control);
        RefreshControlUnsavedIndicators(control.Id);
        SetStatus(Loc.Text("midi.status.bindingReset", control.Label));
    }

    /// <summary>
    /// Wipes hardware mappings and Sonar routes for the selected device (incl. sibling ports),
    /// then re-applies the factory preset map for that device.
    /// </summary>
    public int ClearBindingsForSelectedDevice()
    {
        if (string.IsNullOrWhiteSpace(SelectedDeviceName))
        {
            SetStatus("midi.status.selectDeviceFirst");
            return 0;
        }

        CancelLearn();
        var deviceName = SelectedDeviceName;
        _bindingDrafts.Clear();
        SetBindingDraftDirty(false);
        var removed = _midi.MappingStore.RemoveMatching(b =>
            DevicesShareProduct(b.DeviceName, deviceName));
        _ccProbes.Clear();

        foreach (var control in Controls.Where(c => !c.IsPlaceholder))
        {
            ResetControlVm(control);
        }

        var seeded = SeedFactoryBindingsForDevices([deviceName]);
        ReloadLayoutForSelectedDevice();

        if (removed == 0 && seeded == 0)
        {
            SetStatus(Loc.Text("midi.status.noBindingsToClear", deviceName));
        }
        else if (seeded > 0)
        {
            SetStatus(Loc.Text("midi.status.bindingsClearedSeeded", removed, deviceName, seeded));
        }
        else
        {
            SetStatus(Loc.Text("midi.status.bindingsCleared", removed, deviceName));
        }

        return removed;
    }
}
